using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Minus700K {
// Every request has its own empty native home. Existing conversations in either
// the desktop home or older Minus700K versions are never opened or resumed.
internal static class CodexStorage {
    const string SessionPrefix="Minus700K-codex-session-";
    internal static string CreateSessionHome() { return Path.Combine(Path.GetTempPath(),SessionPrefix+Guid.NewGuid().ToString("N")); }
    internal static void RemoveSessionHome(string home) {
        if(string.IsNullOrEmpty(home)) return;
        string path=Path.GetFullPath(home),temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\','/');
        string name=Path.GetFileName(path); Guid suffix;
        if(!string.Equals(Path.GetDirectoryName(path),temp,StringComparison.OrdinalIgnoreCase) ||
           !name.StartsWith(SessionPrefix,StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring(SessionPrefix.Length),"N",out suffix)) return;
        try { if(Directory.Exists(path)) Directory.Delete(path,true); }
        catch(IOException) { Log.Write("Codex temporary workspace cleanup failed: IOException"); }
        catch(UnauthorizedAccessException) { Log.Write("Codex temporary workspace cleanup failed: UnauthorizedAccessException"); }
    }
    internal static void Apply(ProcessStartInfo start,string home) {
        string target=Path.GetFullPath(home);
        Directory.CreateDirectory(target);
        // These changes affect only this child process, never the desktop or the
        // user's persistent environment. Pin the DB too: CODEX_HOME alone is not
        // sufficient when a parent process supplies CODEX_SQLITE_HOME.
        start.EnvironmentVariables["CODEX_HOME"]=target;
        start.EnvironmentVariables["CODEX_SQLITE_HOME"]=target;
        start.EnvironmentVariables.Remove("CODEX_THREAD_ID");
        start.EnvironmentVariables.Remove("CODEX_ROLLOUT_TRACE_ROOT");
        start.EnvironmentVariables.Remove("CODEX_INTERNAL_ORIGINATOR_OVERRIDE");
        start.EnvironmentVariables.Remove("CODEX_TUI_RECORD_SESSION");
        start.EnvironmentVariables.Remove("CODEX_TUI_SESSION_LOG_PATH");
        start.EnvironmentVariables["RUST_LOG"]="off";
        start.Arguments+=" -c "+HiddenProcess.Quote("sqlite_home="+JsonData.Serializer().Serialize(target));
        start.Arguments+=" -c "+HiddenProcess.Quote("log_dir="+JsonData.Serializer().Serialize(Path.Combine(target,"log")));
    }
    internal static void ValidateConfig(object config,string home) {
        if(!SamePath(JsonData.Text(config,"sqlite_home"),home) || !SamePath(JsonData.Text(config,"log_dir"),Path.Combine(home,"log")))
            throw new PlanException("storage_mismatch",L10n.T("messages.CodexStorage.001"));
        if(JsonData.Text(JsonData.Get(config,"history"),"persistence")!="none") throw PrivacyError();
    }
    internal static PlanException PrivacyError() { return new PlanException("conversation_storage",L10n.T("messages.CodexStorage.002")); }
    internal static void ValidateThread(object thread) {
        if(!true.Equals(JsonData.Get(thread,"ephemeral")) || JsonData.Get(thread,"path")!=null) throw PrivacyError();
    }
    static bool SamePath(string actual,string expected) {
        try {
            return !string.IsNullOrWhiteSpace(actual) && Path.IsPathRooted(actual) &&
                Path.GetFullPath(actual).TrimEnd('\\','/').Equals(Path.GetFullPath(expected).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase);
        } catch(ArgumentException) { return false; } catch(NotSupportedException) { return false; } catch(PathTooLongException) { return false; }
    }
}
// Codex's SQLite diagnostic sink is independent of RUST_LOG and can contain full
// request/response events even for ephemeral threads. Its current protocol has
// no switch to disable that sink. Before accepting any conversation text, block
// INSERT/UPDATE at the database boundary in this request's new private home.
// Unexpected schemas fail closed. No existing user database is ever modified.
internal sealed class CodexLogGuard : IDisposable {
    readonly FileStream lease;
    CodexLogGuard(FileStream lease) { this.lease=lease; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Row(IntPtr user,int count,IntPtr values,IntPtr names);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)] static extern int sqlite3_open16(string file,out IntPtr db);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db,int milliseconds);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_exec(IntPtr db,byte[] sql,Row callback,IntPtr user,out IntPtr error);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern void sqlite3_free(IntPtr pointer);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
    internal static CodexLogGuard Install(string home) {
        string directory=Path.Combine(home,"log"),path=Path.Combine(directory,"logs_2.sqlite");
        if(!Directory.Exists(directory) || !File.Exists(path) || Directory.GetFiles(directory,"*.sqlite").Length!=1) throw CodexStorage.PrivacyError();
        FileStream lease=null; IntPtr db=IntPtr.Zero;
        try {
            // Deny deletion/replacement for the child's whole lifetime. A future
            // recovery path cannot silently replace the guarded database.
            lease=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            if(sqlite3_open16(path,out db)!=0 || sqlite3_busy_timeout(db,5000)!=0) throw CodexStorage.PrivacyError();
            var tables=Query(db,"SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
            if(!tables.SequenceEqual(new[]{"_sqlx_migrations","logs"})) throw CodexStorage.PrivacyError();
            Exec(db,"BEGIN IMMEDIATE; CREATE TRIGGER minus700k_no_log_insert BEFORE INSERT ON logs BEGIN SELECT RAISE(IGNORE); END; CREATE TRIGGER minus700k_no_log_update BEFORE UPDATE ON logs BEGIN SELECT RAISE(IGNORE); END; COMMIT;");
            if(Query(db,"SELECT name FROM sqlite_master WHERE type='trigger' AND tbl_name='logs' ORDER BY name").Count!=2) throw CodexStorage.PrivacyError();
            return new CodexLogGuard(lease);
        } catch { if(lease!=null) lease.Dispose(); throw CodexStorage.PrivacyError(); }
        finally { if(db!=IntPtr.Zero) sqlite3_close(db); }
    }
    static void Exec(IntPtr db,string sql) { Query(db,sql); }
    static List<string> Query(IntPtr db,string sql) {
        var result=new List<string>(); IntPtr error;
        Row callback=delegate(IntPtr user,int count,IntPtr values,IntPtr names) { if(count>0) result.Add(Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(values))??""); return 0; };
        int code=sqlite3_exec(db,Encoding.UTF8.GetBytes(sql+"\0"),callback,IntPtr.Zero,out error);
        GC.KeepAlive(callback); if(error!=IntPtr.Zero) sqlite3_free(error);
        if(code!=0) throw CodexStorage.PrivacyError(); return result;
    }
    public void Dispose() { lease.Dispose(); }
}
}
