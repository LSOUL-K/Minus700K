using System;
using System.IO;

namespace Minus700K {
internal static class RequestStorage {
    internal static string UserDataRoot {
        get {
            string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if(string.IsNullOrWhiteSpace(local)) throw new IOException(L10n.T("messages.RequestStorage.001"));
            return Path.Combine(local,"Minus700K");
        }
    }
    internal static string RequestsRoot { get { return Path.Combine(UserDataRoot,"Logs","Requests"); } }
    internal static string ForUserData(string localDataRoot) {
        if(string.IsNullOrWhiteSpace(localDataRoot) || !Path.IsPathRooted(localDataRoot)) throw new ArgumentException(L10n.T("messages.RequestStorage.002"),"localDataRoot");
        return Path.GetFullPath(Path.Combine(localDataRoot,"Minus700K","Logs","Requests"));
    }
}
}
