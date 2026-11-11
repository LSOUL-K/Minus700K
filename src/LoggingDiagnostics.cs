using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Minus700K {
internal static class LoggingDiagnostics {
    static void Require(bool value,string description) { if(!value) throw new Exception(description); }
    internal static void AssertNoCopies(string directory) {
        string[] duplicates={"prompt.txt","custom-instructions.txt","initial-answer.txt","answer-assessment.json","answer-schema.json","codex.events.jsonl"};
        Require(!Directory.GetFiles(directory,"*",SearchOption.AllDirectories).Any(p=>duplicates.Contains(Path.GetFileName(p))),"no redundant prompts, native events or intermediate answers remain");
    }
    static void VerifyReadOnlyApplication(string fixture,string requestsRoot,DateTimeOffset started) {
        string applicationRoot=Path.GetFullPath(Path.Combine(fixture,"read-only application 日本語"));
        Require(applicationRoot.StartsWith(Path.GetFullPath(fixture).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"read-only application fixture stays inside test directory");
        string legacy=Path.Combine(applicationRoot,"runtime","requests","legacy-request","request.json"); Directory.CreateDirectory(Path.GetDirectoryName(legacy));
        byte[] legacyBytes=System.Text.Encoding.UTF8.GetBytes("{\"status\":\"completed\",\"fixture\":\"old log must remain\"}"); File.WriteAllBytes(legacy,legacyBytes);
        string previousDirectory=Directory.GetCurrentDirectory();
        DirectorySecurity security=Directory.GetAccessControl(applicationRoot,AccessControlSections.Access);
        byte[] originalSecurity=security.GetSecurityDescriptorBinaryForm(); bool changedSecurity=false;
        try {
            SecurityIdentifier user=WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.Write,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Deny));
            Directory.SetAccessControl(applicationRoot,security); changedSecurity=true;
            bool denied=false; try { File.WriteAllText(Path.Combine(applicationRoot,"write-probe.txt"),"must be denied"); } catch(UnauthorizedAccessException) { denied=true; }
            Require(denied,"application fixture actually denies writes"); Directory.SetCurrentDirectory(applicationRoot);
            foreach(bool keep in new[]{true,false}) {
                string directory;
                using(var journal=new RequestJournal(requestsRoot,keep,started)) {
                    directory=journal.DirectoryPath; RequestJournal.Text(keep,directory,"fixture-marker.txt","written outside the application directory"); journal.Finish("completed",null);
                    Require(Path.GetDirectoryName(directory)==requestsRoot,"read-only application CWD cannot redirect journal output");
                }
                Require(keep?File.Exists(Path.Combine(directory,"request.json")):!Directory.Exists(directory),"retention and cleanup succeed with a read-only application directory");
            }
            Require(File.ReadAllBytes(legacy).SequenceEqual(legacyBytes) && Directory.GetDirectories(Path.Combine(applicationRoot,"runtime","requests")).Length==1,"existing application logs are neither moved nor deleted");
        } finally {
            try { Directory.SetCurrentDirectory(previousDirectory); }
            finally { if(changedSecurity) { var restore=new DirectorySecurity(); restore.SetSecurityDescriptorBinaryForm(originalSecurity,AccessControlSections.Access); Directory.SetAccessControl(applicationRoot,restore); } }
        }
    }
    internal static void Run(string root,Dictionary<string,object> report) {
        string temporaryRoot=Path.GetFullPath(Path.GetTempPath());
        string fixture=Path.GetFullPath(Path.Combine(temporaryRoot,"Minus700K-logging-test-"+Guid.NewGuid().ToString("N")));
        Require(fixture.StartsWith(temporaryRoot.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"logging fixture stays inside temporary directory");
        string localData=Path.Combine(fixture,"Local Data 日本語 Ω"),requestsRoot=RequestStorage.ForUserData(localData);
        string expectedUserRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Minus700K");
        Require(RequestStorage.UserDataRoot==expectedUserRoot && RequestStorage.RequestsRoot==Path.Combine(expectedUserRoot,"Logs","Requests"),"default request logs use current user's LocalApplicationData");
        Require(requestsRoot==Path.Combine(localData,"Minus700K","Logs","Requests") && !Directory.Exists(fixture),"user-data path mapping preserves spaces and Unicode without creating directories");
        DateTimeOffset started=new DateTimeOffset(2026,11,8,12,34,56,TimeSpan.FromHours(9));
        var requestDirectories=new List<string>();
        var serializer=new JavaScriptSerializer();
        string memoryJson=serializer.Serialize(new { limit=3,count=2 });
        var instructions=new InstructionSnapshot { Text="PRIVATE_INSTRUCTIONS",source="config.json",characterCount=20 };
        Require(!serializer.Serialize(instructions).Contains("PRIVATE_INSTRUCTIONS"),"instruction diagnostics contain metadata only");
        int checkedCases=0; string sample="";
        foreach(bool keep in new[]{true,false}) foreach(string status in new[]{"completed","failed","cancelled"}) {
            string directory;
            using(var journal=new RequestJournal(requestsRoot,keep,started)) {
                directory=journal.DirectoryPath; requestDirectories.Add(directory);
                Require(Path.GetDirectoryName(directory)==requestsRoot,"journal uses the explicit requests root without adding runtime/requests");
                WaveRecorder.Save(Path.Combine(directory,"question.wav"),new byte[3200]);
                File.WriteAllText(Path.Combine(directory,"transcript.txt"),"[無音] 元の認識結果");
                RequestJournal.Text(keep,directory,"whisper.stderr.log","synthetic recognition diagnostic");
                RequestJournal.Text(keep,directory,"spoken-answer.txt","読み上げ用に加工した文章。");
                RequestJournal.Text(keep,directory,"question.txt","PRIVATE_QUESTION");
                RequestJournal.Text(keep,directory,"answer.txt","PRIVATE_FULL_ANSWER");
                RequestJournal.Text(keep,directory,"recent-memory.json",memoryJson);
                using(OptionalScreenshot failed=OptionalScreenshot.Take(delegate { throw new InvalidOperationException("撮影失敗のテスト"); },CancellationToken.None)) {
                    journal.Data["screenshot"]=failed; journal.Data["imageAttached"]=false;
                }
                foreach(string attempt in new[]{directory,Path.Combine(directory,"web-retry")}) {
                    string temporary=RequestJournal.Temporary(attempt);
                    using(var bitmap=new Bitmap(16,12)) bitmap.Save(Path.Combine(temporary,"screen.png"),ImageFormat.Png);
                    File.WriteAllText(Path.Combine(temporary,"answer.txt"),"temporary answer");
                    File.WriteAllText(Path.Combine(temporary,"answer-assessment.json"),"temporary assessment");
                    File.WriteAllText(Path.Combine(temporary,"answer-schema.json"),SearchFallback.Schema);
                    Require(File.Exists(Path.Combine(temporary,"screen.png")),"image remains available during request and retry");
                    RequestJournal.Text(keep,attempt,"codex.stderr.log","synthetic Codex stderr");
                }
                journal.Phase("asking");
                journal.Finish(status,status=="completed"?null:status=="cancelled"?(Exception)new OperationCanceledException("diagnostic cancellation"):new InvalidOperationException("diagnostic failure"));
                if(!keep) Require(!File.Exists(Path.Combine(directory,"request.json")),"debug off writes no metadata");
                if(!keep) Require(!File.Exists(Path.Combine(directory,"question.txt")) && !File.Exists(Path.Combine(directory,"answer.txt")),"debug off never writes conversation text");
            }
            if(!keep) Require(!Directory.Exists(directory),"debug off removes all work after "+status);
            else {
                AssertNoCopies(directory);
                Require(!Directory.Exists(Path.Combine(directory,"temporary")) && !Directory.Exists(Path.Combine(directory,"web-retry","temporary")),"temporary image and answer copies are removed after "+status);
                Require(new FileInfo(Path.Combine(directory,"question.wav")).Length>3200,"recording remains after "+status);
                Require(File.ReadAllText(Path.Combine(directory,"transcript.txt")).Contains("[無音]"),"unprocessed recognition output remains");
                Require(File.ReadAllText(Path.Combine(directory,"question.txt"))=="PRIVATE_QUESTION" && File.ReadAllText(Path.Combine(directory,"answer.txt"))=="PRIVATE_FULL_ANSWER","explicit debug saving retains question and complete answer");
                Require(File.Exists(Path.Combine(directory,"whisper.stderr.log")) && File.Exists(Path.Combine(directory,"web-retry","codex.stderr.log")),"process diagnostics remain");
                var saved=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(directory,"request.json")));
                var capture=(Dictionary<string,object>)saved["screenshot"];
                Require((string)saved["status"]==status && saved.ContainsKey("finishedAt") && Convert.ToInt32(saved["schemaVersion"])==4,"status and version survive cleanup");
                Require((string)capture["status"]=="failed" && ((string)capture["error"]).Contains("撮影失敗"),"capture failure details remain");
                if(status!="completed") Require(saved.ContainsKey("error") && (string)saved["errorType"]==(status=="cancelled"?typeof(OperationCanceledException).FullName:typeof(InvalidOperationException).FullName),"distinct failure/cancellation errors remain");
                sample=directory;
            }
            checkedCases++;
        }
        VerifyScreenshots(requestsRoot,started);
        VerifyErrorPrivacy(fixture);
        var simultaneous=Enumerable.Range(0,12).Select(index=>Task.Run(delegate {
            using(var journal=new RequestJournal(requestsRoot,true,started)) {
                RequestJournal.Text(true,journal.DirectoryPath,"fixture-marker.txt",index.ToString()); journal.Finish("completed",null); return journal.DirectoryPath;
            }
        })).ToArray();
        Task.WaitAll(simultaneous); requestDirectories.AddRange(simultaneous.Select(task=>task.Result));
        Require(requestDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Count()==requestDirectories.Count,"same timestamp and concurrent submissions never reuse a request directory");
        var requestIds=new HashSet<string>(StringComparer.Ordinal);
        for(int index=0;index<simultaneous.Length;index++) {
            string directory=simultaneous[index].Result;
            Require(File.ReadAllText(Path.Combine(directory,"fixture-marker.txt"))==index.ToString(),"concurrent request writes remain isolated");
            var metadata=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(directory,"request.json")));
            Require(requestIds.Add((string)metadata["requestId"]) && (string)metadata["status"]=="completed","concurrent request IDs and completion metadata remain distinct");
        }
        VerifyReadOnlyApplication(fixture,requestsRoot,started);
        Require(!Directory.Exists(Path.Combine(requestsRoot,"runtime","requests")),"new request location does not nest the legacy application layout");
        report["cases"]=checkedCases; report["parallelRequests"]=simultaneous.Length; report["sampleDirectory"]=sample;
        report["fixtureRequestsRoot"]=requestsRoot;
        report["checks"]="per-user default path / explicit Unicode fixture root / concurrent timestamp collision isolation / read-only application directory / legacy logs preserved; debug on/off x completed/failed/cancelled; submitted PNG retained only with debug ON; disabled/failed captures have no PNG; audio, raw recognition, capture errors, speech text and stderr retained; main/retry temporary images and answers removed; memory counts and instruction metadata exclude text";
    }
    static void VerifyErrorPrivacy(string fixture) {
        string previous=Log.Root,logRoot=Path.Combine(fixture,"error-privacy"); Directory.CreateDirectory(Path.Combine(logRoot,"runtime"));
        const string sensitive="PRIVATE_UPSTREAM_CONVERSATION_SENTINEL";
        try {
            Log.Root=logRoot; int attempts=0;
            var settings=new Settings { KeepRequestFiles=false,AutoWebSearchFallback=true,WebSearch="on-demand" };
            var result=SearchFallback.Run(settings,SearchPolicy.Decide("on-demand","question"),delegate(SearchDecision search,bool assess) {
                if(++attempts==2) throw new IOException(sensitive);
                return new AnswerDraft { answer="first answer",status="needs_web",reason="fixture" };
            },CancellationToken.None);
            Require(attempts==2 && result.Fallback.outcome=="failed","privacy check exercises a failed upstream retry");
            Require(!File.ReadAllText(Path.Combine(logRoot,"runtime","app.log")).Contains(sensitive),"ordinary operational logs never copy upstream conversation errors");
        } finally { Log.Root=previous; }
    }
    static void VerifyScreenshots(string requestsRoot,DateTimeOffset started) {
        foreach(bool keep in new[]{false,true}) foreach(string status in new[]{"completed","failed","cancelled"}) {
            string directory,path;
            using(var journal=new RequestJournal(requestsRoot,keep,started)) {
                directory=journal.DirectoryPath;
                using(var image=OptionalScreenshot.Take(delegate { var bitmap=new Bitmap(23,17); using(var graphics=Graphics.FromImage(bitmap)) graphics.Clear(Color.CornflowerBlue); return bitmap; },CancellationToken.None)) {
                    path=journal.SaveScreenshot(image); journal.Data["screenshot"]=image; journal.Data["imageAttached"]=image.attached;
                    Require(image.attached && File.Exists(path),"captured image is available to the request before cleanup");
                    Require(path==Path.Combine(keep?directory:journal.TemporaryDirectory,"screen.png"),"debug option selects retained PNG or temporary PNG");
                }
                // The Web retry consumes the same attachment, without another debug copy.
                File.WriteAllText(Path.Combine(RequestJournal.Temporary(Path.Combine(directory,"web-retry")),"answer.txt"),"synthetic answer");
                journal.Finish(status,status=="completed"?null:status=="cancelled"?(Exception)new OperationCanceledException("fixture cancellation"):new IOException("fixture model failure"));
            }
            if(!keep) { Require(!Directory.Exists(directory),"debug OFF removes the screenshot after "+status); continue; }
            Require(File.Exists(path) && Directory.GetFiles(directory,"*.png",SearchOption.AllDirectories).Length==1,"one submitted screenshot remains after "+status+" with debug ON");
            using(var saved=new Bitmap(path)) Require(saved.Width==23 && saved.Height==17 && saved.GetPixel(0,0).ToArgb()==Color.CornflowerBlue.ToArgb(),"retained PNG preserves submitted dimensions and pixels");
            var metadata=JsonData.Serializer().DeserializeObject(File.ReadAllText(Path.Combine(directory,"request.json")));
            Require(JsonData.Text(metadata,"screenshotFile")=="screen.png" && true.Equals(JsonData.Get(metadata,"imageAttached")),"request metadata identifies the retained screenshot");
            Require(!Directory.Exists(Path.Combine(directory,"web-retry","temporary")),"retry temporary outputs are still removed");
            AssertNoCopies(directory);
        }
        foreach(bool keep in new[]{false,true}) foreach(string mode in new[]{"disabled","capture_failed","save_failed"}) {
            string directory;
            using(var journal=new RequestJournal(requestsRoot,keep,started)) {
                directory=journal.DirectoryPath;
                if(mode=="save_failed") Directory.CreateDirectory(Path.Combine(keep?directory:journal.TemporaryDirectory,"screen.png"));
                using(var image=OptionalScreenshot.Take(delegate {
                    if(mode=="capture_failed") throw new IOException("synthetic protected screen");
                    var bitmap=new Bitmap(3,3); bitmap.SetPixel(1,1,Color.White); return bitmap;
                },CancellationToken.None,enabled:mode!="disabled")) {
                    string result=journal.SaveScreenshot(image); journal.Data["screenshot"]=image; journal.Data["imageAttached"]=image.attached;
                    Require(result==null && !image.attached && journal.Data["screenshotFile"]==null,"unavailable screenshots do not report a saved PNG");
                    Require(mode=="disabled"?image.status=="disabled" && !image.Failed:image.Failed,"disabled capture remains distinct from a capture or PNG write failure");
                }
                journal.Finish("completed",null); // Voice-only processing is still allowed.
            }
            if(keep) Require(Directory.GetFiles(directory,"*.png",SearchOption.AllDirectories).Length==0,"no fake or partial PNG is retained for "+mode);
            else Require(!Directory.Exists(directory),"debug OFF removes failed/disabled screenshot work");
        }
    }
}
}
