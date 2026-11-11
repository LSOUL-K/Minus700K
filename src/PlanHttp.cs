using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace Minus700K {
internal sealed class PlanException : Exception {
    internal readonly string Code;
    internal PlanException(string code,string message) : base(message) { Code=code; }
    internal static PlanException FromCode(string code,int status) {
        string message=L10n.T("messages.PlanHttp.001");
        if(code=="subscription_sharing_usage_limit_exceeded" || status==429) message=L10n.T("messages.PlanHttp.002");
        else if(code=="subscription_sharing_user_not_eligible") message=L10n.T("messages.PlanHttp.003");
        else if(code=="subscription_sharing_unsupported_capability") message=L10n.T("messages.PlanHttp.004");
        else if(status==401 || code=="invalid_grant" || code=="invalid_refresh_token" || code=="token_expired" || code=="refresh_token_expired" || code=="refresh_token_invalidated" || code=="refresh_token_reused") message=L10n.T("messages.PlanHttp.005");
        else if(status==403) message=L10n.T("messages.PlanHttp.006");
        else if(status>=500) message=L10n.T("messages.PlanHttp.007");
        return new PlanException(string.IsNullOrEmpty(code)?"http_"+status:code,message);
    }
}
internal static class PlanHttp {
    internal const string Issuer="https://auth.openai.com",Resource="https://api.openai.com/v1";
    internal static string Form(IEnumerable<KeyValuePair<string,string>> data) {
        return string.Join("&",data.Where(p=>p.Value!=null).Select(p=>Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(p.Value)).ToArray());
    }
    internal static Dictionary<string,string> Query(string text) {
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string part in text.TrimStart('?').Split('&')) {
            if(part.Length==0) continue; int equals=part.IndexOf('=');
            string key=Uri.UnescapeDataString((equals<0?part:part.Substring(0,equals)).Replace("+"," "));
            string value=Uri.UnescapeDataString((equals<0?"":part.Substring(equals+1)).Replace("+"," "));
            if(values.ContainsKey(key)) throw new PlanException("invalid_callback",L10n.T("messages.PlanHttp.008"));
            values.Add(key,value);
        }
        return values;
    }
    internal static object Json(string url,string method,string body,string accessToken,CancellationToken token) {
        string text=Send(url,method,body,accessToken,token);
        if(string.IsNullOrWhiteSpace(text)) return new Dictionary<string,object>();
        try { return JsonData.Serializer().DeserializeObject(text); }
        catch { throw new PlanException("invalid_response",L10n.T("messages.PlanHttp.009")); }
    }
    internal static string Send(string url,string method,string body,string accessToken,CancellationToken token) {
        Uri uri=new Uri(url);
        if(uri.Scheme!="https" || (uri.Host!="auth.openai.com" && uri.Host!="api.openai.com") || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("Untrusted authentication endpoint.");
        if(!string.IsNullOrEmpty(accessToken) && uri.Host!="api.openai.com") throw new InvalidOperationException("Unexpected bearer destination.");
        token.ThrowIfCancellationRequested(); ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
        HttpWebRequest request=(HttpWebRequest)WebRequest.Create(uri);
        request.Method=method; request.Timeout=30000; request.ReadWriteTimeout=30000; request.AllowAutoRedirect=false;
        request.UserAgent="Minus700K/2.0"; request.Accept="application/json";
        if(accessToken!=null) request.Headers[HttpRequestHeader.Authorization]="Bearer "+accessToken;
        using(token.Register(delegate { request.Abort(); })) {
            try {
                if(body!=null) {
                    byte[] bytes=Encoding.UTF8.GetBytes(body); request.ContentType="application/x-www-form-urlencoded"; request.ContentLength=bytes.Length;
                    using(Stream output=request.GetRequestStream()) output.Write(bytes,0,bytes.Length);
                }
                using(HttpWebResponse response=(HttpWebResponse)request.GetResponse()) {
                    if((int)response.StatusCode>=300) throw PlanException.FromCode("redirect_rejected",(int)response.StatusCode);
                    return Read(response.GetResponseStream());
                }
            } catch(WebException e) {
                token.ThrowIfCancellationRequested(); string code="network_error"; int status=0;
                using(HttpWebResponse response=e.Response as HttpWebResponse) {
                    if(response!=null) {
                        status=(int)response.StatusCode;
                        try { object value=JsonData.Serializer().DeserializeObject(Read(response.GetResponseStream())); object error=JsonData.Get(value,"error"); code=error is string?(string)error:JsonData.Text(error,"code"); } catch { }
                    }
                }
                // Never put token endpoint bodies, authorization URLs, or credentials into exceptions/logs.
                throw PlanException.FromCode(code,status);
            }
        }
    }
    static string Read(Stream stream) {
        using(StreamReader reader=new StreamReader(stream,Encoding.UTF8)) {
            char[] buffer=new char[4096]; StringBuilder text=new StringBuilder(); int count;
            while((count=reader.Read(buffer,0,buffer.Length))>0) { if(text.Length+count>2*1024*1024) throw new PlanException("response_too_large",L10n.T("messages.PlanHttp.010")); text.Append(buffer,0,count); }
            return text.ToString();
        }
    }
}
}
