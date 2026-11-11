using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Minus700K {
internal sealed class PlanModel { internal string Slug,Name; internal Dictionary<string,object> CatalogEntry; public override string ToString() { return Name; } }
internal sealed class ChatGptPlan {
    internal const string AppName="Minus700K",UsageUrl="https://chatgpt.com/settings/usage";
    const string Scope="openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    readonly PlanStore store; readonly int loginTimeoutMilliseconds;
    readonly Func<string,string,string,string,CancellationToken,object> request;
    internal ChatGptPlan(PlanStore store=null,Func<string,string,string,string,CancellationToken,object> request=null,int loginTimeoutMilliseconds=300000) { this.store=store??PlanStore.Default; this.request=request??PlanHttp.Json; this.loginTimeoutMilliseconds=loginTimeoutMilliseconds; }
    internal static string Base64Url(byte[] bytes) { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_'); }
    internal static byte[] Decode(string value) { value=value.Replace('-','+').Replace('_','/'); return Convert.FromBase64String(value+new string('=',(4-value.Length%4)%4)); }
    static string RandomValue() { byte[] bytes=new byte[32]; using(RandomNumberGenerator rng=RandomNumberGenerator.Create()) rng.GetBytes(bytes); return Base64Url(bytes); }
    internal static string Challenge(string verifier) { using(SHA256 hash=SHA256.Create()) return Base64Url(hash.ComputeHash(Encoding.ASCII.GetBytes(verifier))); }
    internal static long Now { get { return (long)(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds; } }
    internal static bool Equal(string a,string b) { if(a==null || b==null) return false; int diff=a.Length^b.Length; for(int i=0;i<Math.Min(a.Length,b.Length);i++) diff|=a[i]^b[i]; return diff==0; }
    static long Number(object value,string key) { try { return Convert.ToInt64(JsonData.Get(value,key)); } catch { return 0; } }
    internal static Dictionary<string,object> ValidateIdToken(string jwt,string clientId,string nonce,string previousSubject,object jwks,long now) {
        try {
            string[] parts=jwt.Split('.'); if(parts.Length!=3) throw new Exception();
            object header=JsonData.Serializer().DeserializeObject(Encoding.UTF8.GetString(Decode(parts[0])));
            if(JsonData.Text(header,"alg")!="RS256") throw new Exception();
            string kid=JsonData.Text(header,"kid"); if(kid.Length==0) throw new Exception();
            var keys=JsonData.Items(JsonData.Get(jwks,"keys")).Where(k=>JsonData.Text(k,"kid")==kid && JsonData.Text(k,"kty")=="RSA").ToArray();
            if(keys.Length!=1) throw new Exception(); object key=keys[0];
            if(JsonData.Text(key,"use").Length>0 && JsonData.Text(key,"use")!="sig") throw new Exception();
            if(JsonData.Text(key,"alg").Length>0 && JsonData.Text(key,"alg")!="RS256") throw new Exception();
            byte[] digest; using(SHA256 hash=SHA256.Create()) digest=hash.ComputeHash(Encoding.ASCII.GetBytes(parts[0]+"."+parts[1]));
            using(RSACryptoServiceProvider rsa=new RSACryptoServiceProvider()) {
                rsa.PersistKeyInCsp=false; rsa.ImportParameters(new RSAParameters { Modulus=Decode(JsonData.Text(key,"n")),Exponent=Decode(JsonData.Text(key,"e")) });
                if(!rsa.VerifyHash(digest,CryptoConfig.MapNameToOID("SHA256"),Decode(parts[2]))) throw new Exception();
            }
            var claims=JsonData.Object(JsonData.Serializer().DeserializeObject(Encoding.UTF8.GetString(Decode(parts[1]))));
            if(JsonData.Text(claims,"iss")!=PlanHttp.Issuer || Number(claims,"exp")<=now-60 || Number(claims,"nbf")>now+60 || Number(claims,"iat")>now+60) throw new Exception();
            object audience=JsonData.Get(claims,"aud"); string[] audiences=audience is string?new[]{(string)audience}:JsonData.Items(audience).Select(Convert.ToString).ToArray();
            string azp=JsonData.Text(claims,"azp");
            if(!audiences.Contains(clientId) || (audiences.Length>1 && azp!=clientId) || (azp.Length>0 && azp!=clientId)) throw new Exception();
            if(nonce!=null && !Equal(JsonData.Text(claims,"nonce"),nonce)) throw new Exception();
            string subject=JsonData.Text(claims,"sub");
            if(subject.Length==0 || (!string.IsNullOrEmpty(previousSubject) && subject!=previousSubject)) throw new Exception();
            return claims;
        } catch { throw new PlanException("invalid_identity",L10n.T("messages.ChatGptPlan.001")); }
    }
    object Jwks(CancellationToken token) { return request(PlanHttp.Issuer+"/.well-known/jwks.json","GET",null,null,token); }
    internal static Uri Authorize(string hostId,PlanAccount account,string callback,string state,string nonce,string verifier,bool consent) {
        var query=new Dictionary<string,string> {
            {"client_id",account==null?"dynamic_agent_client":account.ClientId},{"agent_name_hint",account==null?AppName:null},
            {"ext_agent_host_id",hostId},{"response_type","code"},{"redirect_uri",callback},{"scope",Scope},{"resource",PlanHttp.Resource},
            {"state",state},{"nonce",nonce},{"code_challenge_method","S256"},{"code_challenge",Challenge(verifier)},
            {"id_token_hint",account!=null && account.IdToken.Length>0?account.IdToken:null},
            {"login_hint",account!=null && account.Email.Length>0?account.Email:null},{"prompt",consent?"consent":null}
        };
        return new Uri(PlanHttp.Issuer+"/api/accounts/authorize?"+PlanHttp.Form(query));
    }
    internal static string CallbackClient(Dictionary<string,string> values,string expectedState,string priorClient) {
        string state; if(!values.TryGetValue("state",out state) || !Equal(state,expectedState)) throw new PlanException("invalid_state",L10n.T("messages.ChatGptPlan.002"));
        string error; if(values.TryGetValue("error",out error)) throw new PlanException("login_declined",L10n.T("messages.ChatGptPlan.003"));
        string code; if(!values.TryGetValue("code",out code) || code.Length==0) throw new PlanException("missing_code",L10n.T("messages.ChatGptPlan.004"));
        string client; values.TryGetValue("client_id",out client);
        if(priorClient!=null) { if(!string.IsNullOrEmpty(client) && client!=priorClient) throw new PlanException("client_mismatch",L10n.T("messages.ChatGptPlan.005")); return priorClient; }
        if(string.IsNullOrEmpty(client) || client=="dynamic_agent_client") throw new PlanException("missing_client",L10n.T("messages.ChatGptPlan.006"));
        if(client.Length>256 || client.Any(c=>!char.IsLetterOrDigit(c) && c!='_' && c!='-')) throw new PlanException("invalid_client",L10n.T("messages.ChatGptPlan.007"));
        return client;
    }
    internal static PlanAccount LoginAccount(PlanState state) {
        // Keep old registrations/history intact, but never offer account addition
        // from this single-account UI. An interrupted first registration is reused.
        return state.Active??state.Accounts.LastOrDefault(a=>a.Subject.Length==0)??state.Accounts.FirstOrDefault();
    }
    internal static bool NeedsConsent(PlanAccount account) { return account!=null && account.Subject.Length>0 && account.AccessToken.Length>0 && !account.Permitted; }
    internal PlanAccount SignInCurrent(Action<Uri> openBrowser,CancellationToken token) {
        token.ThrowIfCancellationRequested(); PlanAccount account=LoginAccount(store.Read());
        return SignIn(account==null?null:account.ClientId,NeedsConsent(account),openBrowser,token);
    }
    internal PlanAccount SignIn(string selectedClient,bool consent,Action<Uri> openBrowser,CancellationToken token) {
        token.ThrowIfCancellationRequested();
        string hostId=store.EnsureHost(); PlanState existing=store.Read();
        PlanAccount prior=selectedClient==null?null:existing.Accounts.FirstOrDefault(a=>a.ClientId==selectedClient);
        if(selectedClient!=null && prior==null) throw new InvalidOperationException("Unknown account.");
        string state=RandomValue(),nonce=RandomValue(),verifier=RandomValue();
        TcpListener listener=new TcpListener(IPAddress.Loopback,0); listener.Start(4); PlanAccount committedAccount=null;
        try {
            int port=((IPEndPoint)listener.LocalEndpoint).Port; string callback="http://127.0.0.1:"+port+"/auth/callback";
            token.ThrowIfCancellationRequested();
            openBrowser(Authorize(hostId,prior,callback,state,nonce,verifier,consent));
            Stopwatch wait=Stopwatch.StartNew();
            while(wait.ElapsedMilliseconds<loginTimeoutMilliseconds) {
                token.ThrowIfCancellationRequested(); if(!listener.Pending()) { if(token.WaitHandle.WaitOne(50)) token.ThrowIfCancellationRequested(); continue; }
                using(TcpClient client=listener.AcceptTcpClient())
                using(token.Register(delegate { client.Close(); })) {
                    client.ReceiveTimeout=2000; client.SendTimeout=2000;
                    using(NetworkStream stream=client.GetStream()) {
                        Dictionary<string,string> values;
                        try { values=ReadCallback(stream,port); } catch(IOException) { continue; } catch(PlanException) { Reply(stream,false); continue; }
                        string receivedState; if(!values.TryGetValue("state",out receivedState) || !Equal(receivedState,state)) { Reply(stream,false); continue; }
                        try {
                            string issued=CallbackClient(values,state,prior==null?null:prior.ClientId);
                            PlanAccount registered=prior??store.Read().Accounts.FirstOrDefault(a=>a.ClientId==issued);
                            // Retain registration before exchanging the single-use code. Never activate it until validated.
                            store.Locked(delegate { token.ThrowIfCancellationRequested(); PlanState saved=store.ReadUnlocked(); if(!saved.Accounts.Any(a=>a.ClientId==issued)) { saved.Accounts.Add(new PlanAccount { ClientId=issued }); store.SaveUnlocked(saved); } return true; });
                            object response=request(PlanHttp.Issuer+"/api/accounts/oauth/token","POST",PlanHttp.Form(new Dictionary<string,string> {
                                {"grant_type","authorization_code"},{"client_id",issued},{"code",values["code"]},{"code_verifier",verifier},{"redirect_uri",callback},{"resource",PlanHttp.Resource}
                            }),null,token);
                            PlanAccount account=CompleteTokens(response,issued,nonce,registered,false,token);
                            store.Locked(delegate {
                                PlanState saved=store.ReadUnlocked(); saved.Accounts.RemoveAll(a=>a.ClientId==issued); saved.Accounts.Add(account); saved.ActiveClientId=issued;
                                // Cancellation wins until this commit boundary. Once the
                                // atomic save begins, return success so the UI reports the
                                // credential change even if Cancel arrives during the write.
                                token.ThrowIfCancellationRequested();
                                store.SaveUnlocked(saved); return true;
                            });
                            committedAccount=account; Reply(stream,true); return account;
                        } catch { Reply(stream,false); throw; }
                    }
                }
            }
            throw new PlanException("login_timeout",L10n.T("messages.ChatGptPlan.008"));
        } catch(Exception) { if(committedAccount!=null) return committedAccount; token.ThrowIfCancellationRequested(); throw; }
        finally { listener.Stop(); }
    }
    static Dictionary<string,string> ReadCallback(NetworkStream stream,int port) {
        var bytes=new List<byte>(); int next;
        while(bytes.Count<16384 && (next=stream.ReadByte())>=0) { bytes.Add((byte)next); int n=bytes.Count; if(n>=4 && bytes[n-4]==13 && bytes[n-3]==10 && bytes[n-2]==13 && bytes[n-1]==10) break; }
        string request=Encoding.ASCII.GetString(bytes.ToArray()); string[] lines=request.Split(new[]{"\r\n"},StringSplitOptions.None);
        string[] first=lines[0].Split(' ');
        if(first.Length!=3 || first[0]!="GET" || !first[1].StartsWith("/auth/callback?",StringComparison.Ordinal) || !lines.Any(l=>l.Equals("Host: 127.0.0.1:"+port,StringComparison.OrdinalIgnoreCase))) throw new PlanException("invalid_callback",L10n.T("messages.ChatGptPlan.009"));
        return PlanHttp.Query(first[1].Substring(first[1].IndexOf('?')));
    }
    static void Reply(NetworkStream stream,bool success) {
        try {
            string body="<!doctype html><html lang=\""+L10n.Current.Language+"\"><meta charset=\"utf-8\"><title>Minus700K</title><h1>Minus700K</h1><p>"+System.Net.WebUtility.HtmlEncode(success?L10n.T("messages.ChatGptPlan.010"):L10n.T("messages.ChatGptPlan.011"))+"</p></html>";
            byte[] content=Encoding.UTF8.GetBytes(body); byte[] headers=Encoding.ASCII.GetBytes("HTTP/1.1 "+(success?"200 OK":"400 Bad Request")+"\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nConnection: close\r\nContent-Length: "+content.Length+"\r\n\r\n");
            stream.Write(headers,0,headers.Length); stream.Write(content,0,content.Length);
        } catch(IOException) { } catch(ObjectDisposedException) { }
    }
    PlanAccount CompleteTokens(object response,string clientId,string nonce,PlanAccount prior,bool refresh,CancellationToken token) {
        string access=JsonData.Text(response,"access_token"),id=JsonData.Text(response,"id_token"),rotating=JsonData.Text(response,"refresh_token");
        if(access.Length==0 || !JsonData.Text(response,"token_type").Equals("Bearer",StringComparison.OrdinalIgnoreCase) || Number(response,"expires_in")<=0) throw new PlanException("invalid_token_response",L10n.T("messages.ChatGptPlan.012"));
        if(id.Length==0 && !refresh) throw new PlanException("missing_identity",L10n.T("messages.ChatGptPlan.013"));
        Dictionary<string,object> claims=id.Length>0?ValidateIdToken(id,clientId,nonce,prior==null?null:prior.Subject,Jwks(token),Now):null;
        string scopes=JsonData.Text(response,"scope"); if(refresh && scopes.Length==0 && prior!=null) scopes=prior.Scopes;
        if(refresh && rotating.Length==0) throw new PlanException("missing_refresh_token",L10n.T("messages.ChatGptPlan.014"));
        return new PlanAccount { ClientId=clientId,Subject=claims==null?prior.Subject:JsonData.Text(claims,"sub"),Email=claims==null?prior.Email:JsonData.Text(claims,"email"),
            AccessToken=access,IdToken=id.Length>0?id:prior.IdToken,RefreshToken=rotating,Scopes=scopes,ExpiresAt=Now+Number(response,"expires_in"),
            Welcomed=prior!=null && prior.Welcomed,Model=prior==null?"":prior.Model };
    }
    internal PlanAccount Access(CancellationToken token) {
        return store.Locked(delegate {
            token.ThrowIfCancellationRequested(); PlanState state=store.ReadUnlocked(); PlanAccount active=state.Active;
            if(active==null || !active.Connected) throw new PlanException("login_required",L10n.T("messages.ChatGptPlan.015"));
            if(active.ExpiresAt>Now+120) return active;
            if(active.RefreshToken.Length==0) throw new PlanException("login_required",L10n.T("messages.ChatGptPlan.016"));
            try {
                object response=request(PlanHttp.Issuer+"/api/accounts/oauth/token","POST",PlanHttp.Form(new Dictionary<string,string> {
                    {"grant_type","refresh_token"},{"client_id",active.ClientId},{"refresh_token",active.RefreshToken},{"resource",PlanHttp.Resource}
                }),null,token);
                PlanAccount updated=CompleteTokens(response,active.ClientId,null,active,true,token);
                state.Accounts.RemoveAll(a=>a.ClientId==active.ClientId); state.Accounts.Add(updated); store.SaveUnlocked(state);
                if(!updated.Permitted) throw new PlanException("consent_required",L10n.T("messages.ChatGptPlan.017"));
                return updated;
            } catch(PlanException e) {
                if(new[]{"invalid_grant","invalid_refresh_token","token_expired","refresh_token_expired","refresh_token_invalidated","refresh_token_invalid","refresh_token_reused"}.Contains(e.Code)) { active.ClearTokens(); store.SaveUnlocked(state); }
                throw;
            }
        });
    }
    internal List<PlanModel> Models(PlanAccount account,CancellationToken token) {
        object response=request(PlanHttp.Resource+"/models","GET",null,account.AccessToken,token);
        var models=JsonData.Items(JsonData.Get(response,"models")).Where(m=>JsonData.Text(m,"visibility")=="list").Select(m=>new PlanModel { Slug=JsonData.Text(m,"slug"),Name=JsonData.Text(m,"display_name"),CatalogEntry=JsonData.Object(m) }).Where(m=>m.Slug.Length>0).ToList();
        if(models.Count==0) throw new PlanException("no_models",L10n.T("messages.ChatGptPlan.018"));
        return models;
    }
    internal bool SignOut(string clientId,CancellationToken token) {
        return store.Locked(delegate {
            PlanState state=store.ReadUnlocked(); PlanAccount active=state.Accounts.FirstOrDefault(a=>a.ClientId==clientId);
            if(active==null) return true; bool revoked=active.RefreshToken.Length==0;
            try {
                object metadata=request(PlanHttp.Issuer+"/.well-known/openid-configuration","GET",null,null,token);
                string endpoint=JsonData.Text(metadata,"revocation_endpoint");
                if(!endpoint.StartsWith(PlanHttp.Issuer+"/",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected revocation endpoint.");
                for(int attempt=0;!revoked && attempt<2;attempt++) {
                    try { request(endpoint,"POST",PlanHttp.Form(new Dictionary<string,string> { {"token",active.RefreshToken},{"token_type_hint","refresh_token"},{"client_id",active.ClientId} }),null,token); revoked=true; }
                    catch(PlanException) { if(attempt==0) { if(token.WaitHandle.WaitOne(500)) token.ThrowIfCancellationRequested(); } }
                }
            } catch { /* Clear locally even if remote revocation could not be confirmed. */ }
            active.ClearTokens(); store.SaveUnlocked(state); return revoked;
        });
    }
}
}
