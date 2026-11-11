using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Minus700K {
public sealed class Settings {
    public string PushToTalkKey="Alt+Q",CancelKey="Alt+E",ReplayKey="Alt+R";
    public int MicrophoneDevice=-1,WhisperThreads=2;
    public string SpeakerDevice=""; // Empty follows the existing Windows per-app output preference.
    public string WhisperModel="vendor/ggml-small-q5_1.bin",CodexPath="",CodexModel="",WebSearch="on-demand",GameName="",Language="ja";
    public string AppLanguage="ja"; // UI and assistant language; Language remains the speech-recognition override.
    public string SpeechVoice=""; // Empty selects an installed voice matching AppLanguage.
    public string CustomInstructionsText=CustomInstructions.DefaultText;
    public string WebSearchTriggersText=SearchTriggers.DefaultText;
    public string ApplicationInstructionsText=ApplicationInstructions.DefaultText;
    public bool CustomInstructionsEnabled=true,WebSearchTriggersEnabled=true;
    public int SpeechRate=1,SpeechVolume=85,MaximumRecordingSeconds=60,MinimumRecordingMilliseconds=400,CaptureMaximumWidth=1920,RequestTimeoutSeconds=120,WhisperTimeoutSeconds=90,RememberLastTurns=3;
    public bool KeepRequestFiles=false,PlayCues=true;
    public bool PlayProcessingCues=true;
    public int ProcessingCueIntervalSeconds=3;
    public bool CaptureEnabled=true;
    public bool AnnounceCaptureFailure=false;
    public bool AutoWebSearchFallback=true;
    public int CueVolume=70;
    internal void Validate() {
        AppLanguage=L10n.NormalizeLanguage(AppLanguage);
        SettingsStorage.ValidateTextSettings(this);
        Localizer locale=L10n.For(AppLanguage);
        if(SpeakerDevice==null || SpeakerDevice.Length>2048 || SpeakerDevice.Any(char.IsControl)) throw new Exception(locale.Text("errors.settings.speaker"));
        Hotkey record=Hotkey.Parse(PushToTalkKey),cancel=Hotkey.Parse(CancelKey),replay=Hotkey.Parse(ReplayKey);
        // Polling permits extra modifiers held during play (for example Shift to run).
        // Sharing the primary key would therefore invoke more than one action.
        if(record.Key==cancel.Key || record.Key==replay.Key || cancel.Key==replay.Key) throw new Exception(locale.Text("errors.settings.hotkeyConflict"));
        if(WhisperThreads<1 || WhisperThreads>8 || MaximumRecordingSeconds<1 || MaximumRecordingSeconds>180 || MinimumRecordingMilliseconds<100 || CaptureMaximumWidth<320 || CaptureMaximumWidth>7680 || RequestTimeoutSeconds<10 || RequestTimeoutSeconds>600 || WhisperTimeoutSeconds<10 || SpeechRate < -10 || SpeechRate>10 || SpeechVolume<0 || SpeechVolume>100 || RememberLastTurns<0 || RememberLastTurns>10) throw new Exception(locale.Text("errors.settings.numberRange"));
        if(WebSearch!="on-demand" && WebSearch!="disabled" && WebSearch!="cached" && WebSearch!="live") throw new Exception(locale.Text("errors.settings.webSearch"));
        if(CueVolume<0 || CueVolume>100) throw new Exception(locale.Text("errors.settings.cueVolume"));
        if(ProcessingCueIntervalSeconds<1 || ProcessingCueIntervalSeconds>60) throw new Exception(locale.Text("errors.settings.processingInterval"));
    }
    internal static Settings Load(string root) {
        string text=SettingsStorage.Decode(SettingsStorage.ReadBytes(Path.Combine(root,"config.json")));
        if(string.IsNullOrWhiteSpace(text)) throw new Exception(L10n.T("errors.settings.empty"));
        var json=SettingsStorage.Serializer();
        SettingsStorage.EnsureCurrentSchema(json.Deserialize<Dictionary<string,object>>(text));
        Settings s=json.Deserialize<Settings>(text);
        if(s==null) throw new Exception(L10n.T("errors.settings.empty"));
        s.Validate(); return s;
    }
}
internal sealed class Hotkey {
    internal int Key; internal bool Ctrl,Alt,Shift;
    internal static Hotkey Parse(string value) {
        Hotkey h=new Hotkey();
        foreach(string token in value.Split('+')) {
            string p=token.Trim();
            if(p.Equals("Ctrl",StringComparison.OrdinalIgnoreCase)) h.Ctrl=true;
            else if(p.Equals("Alt",StringComparison.OrdinalIgnoreCase)) h.Alt=true;
            else if(p.Equals("Shift",StringComparison.OrdinalIgnoreCase)) h.Shift=true;
            else { Keys k; if(h.Key!=0 || !Enum.TryParse<Keys>(p,true,out k) || (int)k<1 || (int)k>255) throw new Exception(L10n.F("errors.hotkey.invalid",value)); h.Key=(int)k; }
        }
        if(h.Key==0) throw new Exception(L10n.F("errors.hotkey.missing",value));
        return h;
    }
    internal bool IsDown() { return IsDown(Native.Down); }
    internal bool IsDown(Func<int,bool> read) { return read(Key) && (!Ctrl || read(17)) && (!Alt || read(18)) && (!Shift || read(16)); }
    internal uint Modifiers { get { return 0x4000u | (Alt?1u:0u) | (Ctrl?2u:0u) | (Shift?4u:0u); } }
}
internal static class Log {
    internal static string Root; static readonly object Gate=new object();
    internal static void Write(string text) {
        try { lock(Gate) { string path=Path.Combine(Root,"runtime","app.log"); if(File.Exists(path) && new FileInfo(path).Length>2000000) { File.Copy(path,path+".previous",true); File.WriteAllText(path,""); } File.AppendAllText(path,DateTimeOffset.Now.ToString("o")+" "+text+Environment.NewLine,Encoding.UTF8); } } catch { }
    }
}
internal sealed class ProcessResult { internal string Output,Error; internal int ExitCode; }
internal sealed class ProcessJob : IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct Basic { public long ProcessTime,JobTime; public uint Flags; public UIntPtr Min,Max; public uint Active; public UIntPtr Affinity; public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct Extended { public Basic Basic; public Io Io; public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes,string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job,int type,ref Extended info,uint size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    IntPtr handle;
    internal ProcessJob() { handle=CreateJobObject(IntPtr.Zero,null); Extended e=new Extended(); e.Basic.Flags=0x2000; if(handle==IntPtr.Zero || !SetInformationJobObject(handle,9,ref e,(uint)Marshal.SizeOf(typeof(Extended)))) throw new Exception(L10n.T("errors.process.managementStart")); }
    internal void Add(Process p) { if(!AssignProcessToJobObject(handle,p.Handle)) { try { p.Kill(); } catch { } throw new Exception(L10n.T("errors.process.management")); } }
    public void Dispose() { if(handle!=IntPtr.Zero) { CloseHandle(handle); handle=IntPtr.Zero; } }
}
internal static class HiddenProcess {
    // Windows CommandLineToArgvW escaping; never concatenate a user prompt into shell code.
    internal static string Quote(string s) {
        StringBuilder b=new StringBuilder("\""); int slash=0;
        foreach(char c in s) { if(c=='\\') { slash++; continue; } if(c=='"') { b.Append('\\',slash*2+1); b.Append(c); } else { b.Append('\\',slash); b.Append(c); } slash=0; }
        b.Append('\\',slash*2); b.Append('"'); return b.ToString();
    }
    internal static ProcessResult Run(string exe,IEnumerable<string> args,string cwd,string input,int timeout,CancellationToken token,byte[] binaryInput=null) {
        token.ThrowIfCancellationRequested();
        ProcessStartInfo start=new ProcessStartInfo(exe,string.Join(" ",args.Select(Quote).ToArray())) {
            UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=cwd,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
        };
        start.EnvironmentVariables.Remove("OPENAI_API_KEY"); // This application always uses ChatGPT sign-in.
        using(ProcessJob job=new ProcessJob()) using(Process p=new Process { StartInfo=start }) {
            StringBuilder stdout=new StringBuilder(),stderr=new StringBuilder(); object gate=new object();
            p.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs e) { if(e.Data!=null) lock(gate) { if(stdout.Length<4000000) stdout.AppendLine(e.Data); } };
            p.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e) { if(e.Data!=null) lock(gate) { if(stderr.Length<64000) stderr.AppendLine(e.Data); } };
            p.Start(); job.Add(p); try { p.PriorityClass=ProcessPriorityClass.BelowNormal; } catch { }
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            Task inputTask=null;
            try {
                if(binaryInput!=null) {
                    // Whisper may load its model before reading stdin. Keep cancellation
                    // responsive while a recording larger than the pipe buffer is sent.
                    inputTask=Task.Run(delegate {
                        try { p.StandardInput.BaseStream.Write(binaryInput,0,binaryInput.Length); p.StandardInput.BaseStream.Flush(); }
                        finally { p.StandardInput.Close(); }
                    });
                } else {
                    if(input!=null) { byte[] bytes=new UTF8Encoding(false).GetBytes(input); p.StandardInput.BaseStream.Write(bytes,0,bytes.Length); p.StandardInput.BaseStream.Flush(); }
                    p.StandardInput.Close();
                }
                Stopwatch timer=Stopwatch.StartNew();
                while(!p.WaitForExit(50)) { token.ThrowIfCancellationRequested(); if(timer.Elapsed.TotalSeconds>timeout) throw new TimeoutException(L10n.T("errors.process.timeout")); }
                p.WaitForExit(); token.ThrowIfCancellationRequested();
                if(inputTask!=null && p.ExitCode==0) inputTask.GetAwaiter().GetResult();
                return new ProcessResult { Output=stdout.ToString(),Error=stderr.ToString(),ExitCode=p.ExitCode };
            } finally {
                // Release process-held files before RequestJournal removes its temporary work.
                job.Dispose();
                try { if(!p.HasExited) p.WaitForExit(3000); } catch(InvalidOperationException) { }
                if(inputTask!=null) { try { inputTask.Wait(3000); } catch(AggregateException) { } }
            }
        }
    }
}
internal sealed class Turn { internal string Question,Answer,ContextKey,WindowTitle,Application; internal bool UsedWeb; internal DateTime RecordedUtc=DateTime.UtcNow; }
internal sealed class AssistantService {
    readonly Settings settings; readonly string root,codex,whisper,model;
    readonly Localizer locale;
    readonly LanguageBehavior languageBehavior;
    readonly CustomInstructions customInstructions;
    readonly SearchTriggers searchTriggers;
    readonly ChatGptPlan plan=new ChatGptPlan();
    readonly List<Turn> history=new List<Turn>();
    string historyAccountKey="";
    internal AssistantService(string root,Settings settings) {
        this.root=root; this.settings=settings; locale=L10n.For(settings.AppLanguage); languageBehavior=LanguageBehavior.ForInput(settings); customInstructions=new CustomInstructions(settings.CustomInstructionsEnabled,settings.CustomInstructionsText,locale.Language); codex=FindCodex(settings.CodexPath);
        searchTriggers=new SearchTriggers(settings.WebSearchTriggersEnabled,settings.WebSearchTriggersText,locale.Language,languageBehavior);
        string dir=Path.Combine(root,"vendor","whisper");
        whisper=Directory.Exists(dir)?Directory.GetFiles(dir,"whisper-cli.exe",SearchOption.AllDirectories).FirstOrDefault():null;
        model=Path.GetFullPath(Path.Combine(root,settings.WhisperModel));
        if(string.IsNullOrEmpty(whisper) || !File.Exists(model)) throw new Exception(locale.Text("errors.service.recognitionFiles"));
    }
    internal static string FindCodex(string configured) {
        return FindCodex(configured,Environment.GetEnvironmentVariable("PATH"),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Environment.GetEnvironmentVariable("CODEX_INSTALL_DIR"));
    }
    internal static string FindCodex(string configured,string pathValue,string localData,string installDirectory) {
        if(!string.IsNullOrWhiteSpace(configured)) { if(!File.Exists(configured)) throw new Exception(L10n.T("errors.service.codexPath")); return Path.GetFullPath(configured); }
        foreach(string dir in (pathValue??"").Split(';')) { if(string.IsNullOrWhiteSpace(dir)) continue; try { string p=Path.Combine(dir.Trim().Trim('"'),"codex.exe"); if(File.Exists(p)) return Path.GetFullPath(p); } catch { } }
        foreach(string dir in new[] { installDirectory,Path.Combine(localData,"Programs","OpenAI","Codex","bin") }) {
            if(string.IsNullOrWhiteSpace(dir)) continue;
            string p=Path.Combine(dir,"codex.exe"); if(File.Exists(p)) return Path.GetFullPath(p);
        }
        string baseDir=Path.Combine(localData,"OpenAI","Codex","bin");
        if(Directory.Exists(baseDir)) { string f=Directory.GetFiles(baseDir,"codex.exe",SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault(); if(f!=null) return f; }
        throw new Exception(L10n.T("errors.service.codexMissing"));
    }
    internal string Transcribe(string wav,CancellationToken token) {
        // Windows argv in whisper.cpp cannot represent every user-profile path.
        // Send WAV bytes through stdin and collect text through stdout instead.
        // An explicit non-"-" output basename keeps the segment callback enabled
        // in b5454; without -otxt no output file is written by the native process.
        token.ThrowIfCancellationRequested();
        ProcessResult r=HiddenProcess.Run(whisper,new[] { "-m",WhisperPath(model),"-f","-","-l",settings.Language,"-t",settings.WhisperThreads.ToString(),"-ng","-nt","-of","transcript" },root,null,settings.WhisperTimeoutSeconds,token,File.ReadAllBytes(wav));
        RequestJournal.Text(settings.KeepRequestFiles,Path.GetDirectoryName(wav),"whisper.stdout.log",r.Output);
        RequestJournal.Text(settings.KeepRequestFiles,Path.GetDirectoryName(wav),"whisper.stderr.log",r.Error);
        RequestJournal.Text(settings.KeepRequestFiles,Path.GetDirectoryName(wav),"transcript.txt",r.Output);
        if(r.ExitCode!=0) { Log.Write("whisper error exit_code="+r.ExitCode); throw new Exception(locale.Text("errors.service.transcription")); }
        string q=r.Output.Trim();
        q=languageBehavior.CleanTranscription(q);
        if(q.Length<2) throw new Exception(locale.Text("errors.service.noSpeech"));
        return q;
    }
    string WhisperPath(string path) { string prefix=root.TrimEnd('\\','/')+Path.DirectorySeparatorChar; return path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)?path.Substring(prefix.Length):path; }
    internal SearchDecision DecideSearch(string question) { return SearchPolicy.Decide(settings.WebSearch,question,settings.WebSearch=="on-demand"?searchTriggers.Read():null,languageBehavior); }
    RecentTurn[] CaptureRecentHistory(CancellationToken token) {
        token.ThrowIfCancellationRequested();
        Turn[] recent;
        lock(history) { TrimHistory(); recent=history.ToArray(); }
        // Freeze the same full memory for the initial request and a search retry.
        // Even "ignore previous conversations" is passed to the AI to interpret;
        // setting the memory limit to zero disables retention and transmission.
        return RecentHistory.Capture(settings,recent);
    }
    internal string BuildPrompt(string question,ApplicationInfo app,bool hasImage=true) { return BuildPrompt(question,app,customInstructions.Read(),DecideSearch(question),hasImage,CaptureRecentHistory(CancellationToken.None)); }
    string BuildPrompt(string question,ApplicationInfo app,InstructionSnapshot instructions,SearchDecision search,bool hasImage,RecentTurn[] recentContext) {
        StringBuilder prompt=new StringBuilder();
        prompt.AppendLine(locale.Text("prompts.assistantRole"));
        prompt.AppendLine(locale.Text(hasImage?"prompts.imageAttached":"prompts.noImage"));
        prompt.AppendLine(locale.Text("prompts.assistantRules"));
        prompt.AppendLine(locale.Text("prompts.answerLanguage"));
        string applicationInstructions=LocalizedDefaults.Resolve("defaults.applicationInstructions",settings.ApplicationInstructionsText,locale.Language);
        if(applicationInstructions.Length>0) {
            prompt.AppendLine(locale.Text("prompts.applicationInstructionsStart"));
            prompt.AppendLine(applicationInstructions); prompt.AppendLine(locale.Text("prompts.applicationInstructionsEnd"));
        }
        if(instructions.Text.Length>0) {
            prompt.AppendLine(locale.Text("prompts.customInstructionsStart"));
            prompt.AppendLine(instructions.Text); prompt.AppendLine(locale.Text("prompts.customInstructionsEnd"));
        }
        prompt.AppendLine(locale.Text("prompts.independentQuestion"));
        if(search.Enabled) {
            if(search.reason=="auto_fallback") prompt.AppendLine(locale.Text("prompts.searchRetry"));
            if(search.reason=="voice_opt_in" || search.reason=="keyword_opt_in") prompt.AppendLine(locale.Text("prompts.searchRequested"));
            prompt.AppendLine(locale.Format("prompts.searchEnabled",search.effectiveMode));
        } else {
            prompt.AppendLine(hasImage?locale.Text("prompts.searchDisabledImage"):locale.Text("prompts.searchDisabledVoice"));
        }
        if(!hasImage) prompt.AppendLine(locale.Text("prompts.noImageGuidance"));
        if(!string.IsNullOrWhiteSpace(settings.GameName)) prompt.AppendLine(locale.Text("prompts.supplementalName")+new JavaScriptSerializer().Serialize(settings.GameName));
        prompt.AppendLine(locale.Text("prompts.applicationData")+new JavaScriptSerializer().Serialize(app));
        prompt.AppendLine(locale.Text("prompts.recentHistory"));
        prompt.AppendLine(new JavaScriptSerializer().Serialize(new { limit=settings.RememberLastTurns,exchanges=recentContext }));
        prompt.AppendLine(locale.Text("prompts.confirmationHistory"));
        prompt.AppendLine(locale.Text("prompts.question")+question); return prompt.ToString();
    }
    internal void Remember(ApplicationInfo app,string question,string answer,bool usedWeb=false) {
        lock(history) {
            TrimHistory();
            if(settings.RememberLastTurns<=0) return;
            history.Add(new Turn { ContextKey=app.ContextKey,Question=question,Answer=answer,UsedWeb=usedWeb,
                WindowTitle=app.windowTitle,Application=string.IsNullOrEmpty(app.productName)?app.executableName:app.productName });
            TrimHistory();
        }
    }
    void TrimHistory() {
        int excess=history.Count-Math.Max(0,settings.RememberLastTurns);
        if(excess>0) history.RemoveRange(0,excess);
    }
    internal void UseHistoryAccount(string accountKey) {
        lock(history) { if(historyAccountKey!=accountKey) { history.Clear(); historyAccountKey=accountKey; } }
    }
    // Called only after settings have been successfully applied, with requests idle.
    internal void TransferRecentHistoryFrom(AssistantService previous) {
        if(ReferenceEquals(previous,this)) return;
        lock(previous.history) lock(history) {
            history.Clear(); history.AddRange(previous.history); historyAccountKey=previous.historyAccountKey;
            TrimHistory(); previous.history.Clear();
        }
    }
    internal string Ask(string question,string image,ApplicationInfo app,string work,CancellationToken token,SearchDecision search=null) {
        return AskWithResult(question,image,app,work,token,search).Answer;
    }
    internal AnswerResult AskWithResult(string question,string image,ApplicationInfo app,string work,CancellationToken token,SearchDecision search=null) {
        PlanAccount account=plan.Access(token);
        UseHistoryAccount(account.Key);
        var available=plan.Models(account,token);
        string selectedModel=string.IsNullOrWhiteSpace(settings.CodexModel)?(string.IsNullOrWhiteSpace(account.Model)?available[0].Slug:account.Model):settings.CodexModel;
        if(!available.Any(m=>m.Slug==selectedModel)) throw new PlanException("model_unavailable",locale.Text("errors.service.modelUnavailable"));
        if(search==null) search=DecideSearch(question);
        InstructionSnapshot instructions=customInstructions.Read();
        RecentTurn[] recentContext=CaptureRecentHistory(token);
        RequestJournal.Text(settings.KeepRequestFiles,work,"question.txt",question);
        RequestJournal.Text(settings.KeepRequestFiles,work,"recent-memory.json",new JavaScriptSerializer().Serialize(new { limit=settings.RememberLastTurns,count=recentContext.Length }));
        AnswerResult result=SearchFallback.Run(settings,search,delegate(SearchDecision decision,bool assess) {
            string attemptWork=decision.reason=="auto_fallback"?Path.Combine(work,"web-retry"):work;
            Directory.CreateDirectory(attemptWork);
            AnswerDraft draft=AskOnce(question,image,app,attemptWork,token,decision,instructions,assess,recentContext,account,available.First(m=>m.Slug==selectedModel));
            return draft;
        },token);
        result.RecentMemoryCount=recentContext.Length;
        token.ThrowIfCancellationRequested();
        RequestJournal.Text(settings.KeepRequestFiles,work,"search-fallback.json",new JavaScriptSerializer().Serialize(result.Fallback));
        RequestJournal.Text(settings.KeepRequestFiles,work,"answer.txt",result.Answer);
        Remember(app,question,result.Answer,result.Search.Enabled);
        return result;
    }
    AnswerDraft AskOnce(string question,string image,ApplicationInfo app,string work,CancellationToken token,SearchDecision search,InstructionSnapshot instructions,bool assess,RecentTurn[] recentContext,PlanAccount account,PlanModel selectedModel) {
        PlanAccount renewed=plan.Access(token);
        if(renewed.Key!=account.Key) throw new PlanException("account_changed",locale.Text("errors.service.accountChanged"));
        account=renewed;
        bool hasImage=!string.IsNullOrWhiteSpace(image);
        string prompt=BuildPrompt(question,app,instructions,search,hasImage,recentContext);
        if(assess) prompt+=locale.Text("prompts.assessment");
        RequestJournal.Text(settings.KeepRequestFiles,work,"input-mode.json",new JavaScriptSerializer().Serialize(new { imageAttached=hasImage,mode=hasImage?"image_and_voice":"voice_only" }));
        RequestJournal.Text(settings.KeepRequestFiles,work,"search-policy.json",new JavaScriptSerializer().Serialize(search));
        RequestJournal.Text(settings.KeepRequestFiles,work,"custom-instructions.json",new JavaScriptSerializer().Serialize(instructions));
        Stopwatch timer=Stopwatch.StartNew();
        AnswerDraft draft=CodexPlan.Run(codex,root,settings,prompt,image,search,assess,account,selectedModel,token);
        token.ThrowIfCancellationRequested();
        Log.Write("codex completed via=chatgpt_plan model="+selectedModel.Slug+" elapsed_ms="+timer.ElapsedMilliseconds+" answer_chars="+draft.answer.Length);
        return draft;
    }
    internal void ClearHistory() { lock(history) history.Clear(); }
    internal static string Spoken(string answer,string appLanguage="ja") {
        // Remove the complete inline link, including its label, and separators between
        // consecutive citations. Keep the original answer unchanged for logs and history.
        const string link=@"!?\[(?:\\.|[^\]\\])*\]\((?:\\.|[^()\\]|\([^()]*\))*\)";
        string s=Regex.Replace(answer??"","(?:"+link+@")(?:\s*[・、,，;；/／|｜]\s*(?:"+link+@"))*","");
        s=Regex.Replace(s,@"https?://[^\s<>「」『』。！？]+","");
        s=Regex.Replace(s,@"[*#`_]+","");
        // Strip verification metadata, not dates that explain the answer (release dates,
        // events, or an as-of clause such as "2026年11月8日時点では未発表です").
        s=LanguageBehavior.StripSpeechMetadata(s);
        s=Regex.Replace(s,@"[（(][\s・、,，;；/／|｜]*[）)]","");
        s=LanguageBehavior.StripSpeechHeadings(s);
        s=Regex.Replace(s,@"^[ \t・、,，;；/／|｜\-–—]+(?=\r?$)","",RegexOptions.Multiline);
        s=Regex.Replace(s,@"[ \t]+", " ").Trim();
        return s.Length>700?s.Substring(0,700)+LanguageBehavior.For(appLanguage).Text("speech.sentenceEnd"):s;
    }
}
}
