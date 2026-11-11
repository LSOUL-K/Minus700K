using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Minus700K {
internal sealed class PlanAccount {
    public string ClientId="",Subject="",Email="",IdToken="",AccessToken="",RefreshToken="",Scopes="",Model="";
    public long ExpiresAt;
    public bool Welcomed;
    internal string Key { get { return ClientId+":"+Subject; } }
    internal bool Permitted { get { return Scopes.Split(' ').Contains("chatgpt.tokens.use.direct") && Scopes.Split(' ').Contains("resource.invoke"); } }
    internal bool Connected { get { return AccessToken.Length>0 && Permitted; } }
    internal void ClearTokens() { AccessToken=""; RefreshToken=""; IdToken=""; ExpiresAt=0; }
}
internal sealed class PlanState {
    public string HostId="",ActiveClientId="";
    public List<PlanAccount> Accounts=new List<PlanAccount>();
    internal PlanAccount Active { get { return Accounts.FirstOrDefault(a=>a.ClientId==ActiveClientId); } }
}
internal sealed class PlanStore {
    internal static readonly PlanStore Default=new PlanStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Minus700K","connection"));
    readonly string directory,path,mutexName;
    internal PlanStore(string directory) {
        this.directory=Path.GetFullPath(directory); path=Path.Combine(this.directory,"chatgpt.dat");
        using(SHA256 hash=SHA256.Create()) mutexName="Local\\Minus700K_Auth_"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-","").Substring(0,24);
    }
    internal T Locked<T>(Func<T> action) {
        using(Mutex gate=new Mutex(false,mutexName)) {
            bool acquired=false;
            try { try { acquired=gate.WaitOne(45000); } catch(AbandonedMutexException) { acquired=true; } if(!acquired) throw new PlanException("auth_busy",L10n.T("messages.PlanStore.002")); return action(); }
            finally { if(acquired) gate.ReleaseMutex(); }
        }
    }
    internal PlanState Read() { return Locked(ReadUnlocked); }
    internal PlanState ReadUnlocked() {
        if(!File.Exists(path)) return new PlanState();
        try {
            byte[] plain=ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser);
            try { PlanState value=JsonData.Serializer().Deserialize<PlanState>(Encoding.UTF8.GetString(plain)); if(value==null || value.Accounts==null) throw new InvalidDataException(); return value; }
            finally { Array.Clear(plain,0,plain.Length); }
        } catch { throw new PlanException("credential_store",L10n.T("messages.PlanStore.003")); }
    }
    internal void SaveUnlocked(PlanState state) {
        Directory.CreateDirectory(directory);
        SecurityIdentifier sid=WindowsIdentity.GetCurrent().User;
        DirectorySecurity acl=new DirectorySecurity(); acl.SetAccessRuleProtection(true,false); acl.SetOwner(sid);
        acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        Directory.SetAccessControl(directory,acl);
        byte[] plain=Encoding.UTF8.GetBytes(JsonData.Serializer().Serialize(state));
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            File.WriteAllBytes(temp,ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser));
            if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
        } finally { Array.Clear(plain,0,plain.Length); if(File.Exists(temp)) File.Delete(temp); }
    }
    internal string EnsureHost() {
        return Locked(delegate { PlanState state=ReadUnlocked(); if(state.HostId.Length==0) { state.HostId="urn:uuid:"+Guid.NewGuid().ToString(); SaveUnlocked(state); } return state.HostId; });
    }
    internal void Select(string clientId) {
        Locked(delegate { PlanState state=ReadUnlocked(); if(!state.Accounts.Any(a=>a.ClientId==clientId && a.Subject.Length>0)) throw new InvalidOperationException("Unknown account."); state.ActiveClientId=clientId; SaveUnlocked(state); return true; });
    }
}
}
