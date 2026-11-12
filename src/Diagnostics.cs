using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Minus700K {
internal static class Diagnostics {
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string command,out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr ptr);
    static void Require(bool condition,string message) { if(!condition) throw new Exception(message); }
    internal static int Run(string root,string[] args) {
        if(args[0]=="--sign-in" || args[0]=="--settings") {
            try { using(EventWaitHandle connection=EventWaitHandle.OpenExisting(args[0]=="--sign-in"?ResidentIpc.ConnectEvent:ResidentIpc.SettingsEvent)) connection.Set(); return 0; } catch(WaitHandleCannotBeOpenedException) { }
            System.Windows.Forms.Application.EnableVisualStyles();
            using(SettingsForm form=new SettingsForm(root,Settings.Load(root),null,args[0]=="--sign-in"?5:0))
            using(var update=new UpdateShutdown(()=>form.ShutdownReady,form.BeginUpdateWait,form.EndUpdateWait,form.CloseForUpdate)) System.Windows.Forms.Application.Run(form);
            return 0;
        }
        if(args[0]=="--stop") { try { using(EventWaitHandle stop=EventWaitHandle.OpenExisting(ResidentIpc.StopEvent)) stop.Set(); } catch(WaitHandleCannotBeOpenedException) {} return 0; }
        if(args[0]=="--child-wait") { Thread.Sleep(15000); return 0; }
        if(args[0]=="--child-input-hash") {
            using(var hash=System.Security.Cryptography.SHA256.Create()) Console.WriteLine(BitConverter.ToString(hash.ComputeHash(Console.OpenStandardInput())));
            return 0;
        }
        if(args[0]=="--child-exit") { return 7; }
        string reportFile=Path.Combine(root,"runtime",args[0].TrimStart('-')+".json");
        Dictionary<string,object> report=new Dictionary<string,object>(); Stopwatch total=Stopwatch.StartNew();
        IntPtr initial=Native.GetForegroundWindow(); int focusChanges=0; bool monitor=true;
        Thread observer=new Thread(delegate() { while(Volatile.Read(ref monitor)) { if(Native.GetForegroundWindow()!=initial) Interlocked.Increment(ref focusChanges); Thread.Sleep(10); } }) { IsBackground=true }; observer.Start();
        try {
            Settings settings=Settings.Load(root);
            report["mode"]=args[0]; report["microphones"]=WaveRecorder.Devices();
            using(SpeechSynthesizer voice=Speech.Create(settings)) report["voice"]=voice.Voice.Name;
            if(args[0]=="--localization-test") {
                LocalizationDiagnostics.Run(root,report);
            } else if(args[0]=="--english-behavior-test") {
                EnglishBehaviorDiagnostics.Run(report);
            } else if(args[0]=="--settings-storage-test") {
                SettingsStorageDiagnostics.Run(root,report);
            } else if(args[0]=="--settings-ui-test") {
                SettingsUiDiagnostics.Run(root,report);
            } else if(args[0]=="--model-settings-test") {
                ModelSettingsDiagnostics.Run(root,report);
            } else if(args[0]=="--speaker-output-test") {
                SpeakerDiagnostics.Run(settings,report);
            } else if(args[0]=="--other-audio-test") {
                OtherApplicationAudioDiagnostics.Run(root,report);
            } else if(args[0]=="--exclusive-microphone-test") {
                ExclusiveMicrophoneDiagnostics.Run(root,report);
            } else if(args[0]=="--plan-auth-test") {
                PlanDiagnostics.Run(report);
            } else if(args[0]=="--plan-transport-test") {
                PlanDiagnostics.Transport(root,settings,report);
            } else if(args[0]=="--codex-storage-test") {
                CodexStorageDiagnostics.Run(root,settings,report);
            } else if(args[0]=="--plan-status") {
                PlanDiagnostics.Status(report);
            } else if(args[0]=="--mixer-audio-test") {
                using(CuePlayer cues=new CuePlayer(settings.CueVolume)) using(SpeechSynthesizer voice=Speech.Create(settings)) {
                    Require(cues.WaitReady(5000),"mixer test output: "+cues.Error);
                    Thread.Sleep(1500); // Allow the mixer identity watcher to label the new session.
                    int last=cues.Play(660); Thread.Sleep(600);
                    Require(cues.CompletedId>=last && cues.Error=="","cue completed");
                    voice.Speak("Minus700Kの音声出力を確認しています。");
                    Thread.Sleep(3000); // Allow an external Core Audio probe to inspect the session.
                    report["outputDevice"]=cues.DeviceName;
                    report["completedCues"]=cues.CompletedId;
                    report["checks"]="cue completion / speech completion / mixer inspection window";
                }
            } else if(args[0]=="--processing-cue-test" || args[0]=="--processing-cue-audio-test") {
                ProcessingCueDiagnostics.Run(settings,report,args[0]=="--processing-cue-audio-test");
            } else if(args[0]=="--history-isolation-test" || args[0]=="--history-isolation-live-test") {
                HistoryIsolationDiagnostics.Run(root,settings,report,args[0]=="--history-isolation-live-test");
            } else if(args[0]=="--recent-history-test") {
                RecentHistoryDiagnostics.Run(root,settings,report);
            } else if(args[0]=="--search-fallback-test") {
                Settings auto=new Settings { AutoWebSearchFallback=true,WebSearch="on-demand" };
                SearchDecision initialDecision=SearchPolicy.Decide("on-demand","この商品の価格は？");
                int calls=0;
                foreach(string status in new[]{"answered","needs_clarification"}) {
                    calls=0;
                    AnswerResult kept=SearchFallback.Run(auto,initialDecision,delegate(SearchDecision decision,bool assess) { calls++; Require(assess && !decision.Enabled,"initial assessment never searches"); return new AnswerDraft { answer="画面の説明です。",status=status,reason="test" }; },CancellationToken.None);
                    Require(calls==1 && !kept.Fallback.attempted && kept.Answer=="画面の説明です。","useful answer or missing target is not retried");
                }
                calls=0;
                AnswerResult retried=SearchFallback.Run(auto,initialDecision,delegate(SearchDecision decision,bool assess) {
                    calls++;
                    if(calls==1) { Require(assess && !decision.Enabled,"assess offline first"); return new AnswerDraft { answer="価格を確認できません。",status="needs_web",reason="型番は分かるが価格がない" }; }
                    Require(!assess && decision.effectiveMode=="live" && decision.reason=="auto_fallback","one live retry without recursive assessment");
                    return new AnswerDraft { answer="調べ直した回答です。",status="needs_web",reason="test",WebSearchCalled=true };
                },CancellationToken.None);
                Require(calls==2 && retried.Answer=="調べ直した回答です。" && retried.Fallback.webSearchCalled && retried.Search.Enabled,"only final answer is returned");
                Require(!initialDecision.Enabled && auto.WebSearch=="on-demand","retry does not mutate initial decision or defaults");
                foreach(string mode in new[]{"on-demand","disabled","live","cached"}) {
                    auto.WebSearch=mode;
                    SearchDecision decision=SearchPolicy.Decide(mode,mode=="on-demand"?"検索しないで、画像だけで教えて":"質問");
                    calls=0;
                    AnswerResult blocked=SearchFallback.Run(auto,decision,delegate(SearchDecision current,bool assess) { calls++; Require(!assess,"no assessment when ineligible"); return new AnswerDraft { answer="回答",status="needs_web",reason="test" }; },CancellationToken.None);
                    Require(calls==1 && !blocked.Fallback.attempted,"opt-out and fixed modes never auto-retry: "+mode);
                }
                auto.WebSearch="on-demand"; auto.AutoWebSearchFallback=false;
                Require(!SearchFallback.Eligible(auto,initialDecision),"setting disables fallback"); auto.AutoWebSearchFallback=true;
                calls=0;
                AnswerResult failed=SearchFallback.Run(auto,initialDecision,delegate(SearchDecision decision,bool assess) { calls++; if(calls==2) throw new TimeoutException("test timeout"); return new AnswerDraft { answer="元の回答",status="needs_web",reason="test" }; },CancellationToken.None);
                Require(calls==2 && failed.Fallback.outcome=="failed" && failed.Answer.StartsWith("元の回答") && failed.Answer.Contains("再確認はできません"),"retry failure preserves first answer and explains failure");
                foreach(bool duringRetry in new[]{false,true}) using(CancellationTokenSource cancellation=new CancellationTokenSource()) {
                    calls=0; bool cancelled=false;
                    try { SearchFallback.Run(auto,initialDecision,delegate(SearchDecision decision,bool assess) { calls++; if(!duringRetry || calls==2) cancellation.Cancel(); return new AnswerDraft { answer="回答",status="needs_web",reason="test" }; },cancellation.Token); } catch(OperationCanceledException) { cancelled=true; }
                    Require(cancelled && calls==(duringRetry?2:1),"cancellation propagates before or during retry");
                }
                AnswerDraft parsed=SearchFallback.Parse("{\"answer\":\"短い回答。\",\"status\":\"answered\",\"reason\":\"画面に記載\"}");
                Require(parsed.answer=="短い回答。","only answer text reaches speech");
                bool invalid=false; try { SearchFallback.Parse("{\"answer\":\"回答\",\"status\":\"unexpected\",\"reason\":\"test\"}"); } catch(Exception) { invalid=true; }
                Require(invalid,"invalid assessment cannot enable search");
                report["checks"]="useful answer / unknown target / one retry / final answer only / default reset / explicit opt-out / disabled setting / fixed modes / retry error / cancellation / structured output";
            } else if(args[0]=="--auto-search-live-test") {
                settings.AutoWebSearchFallback=true; settings.WebSearch="on-demand"; settings.KeepRequestFiles=true;
                string dir=Path.Combine(root,"runtime","auto-search-live-test-work",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                string png=Path.Combine(dir,"screen.png");
                using(Bitmap bitmap=new Bitmap(1000,400)) using(Graphics g=Graphics.FromImage(bitmap)) using(Font font=new Font("Yu Gothic UI",30)) {
                    g.Clear(Color.White); g.DrawString("Nintendo Switch 2\n日本語・国内専用\nメーカー：任天堂",font,Brushes.Black,40,40); bitmap.Save(png,ImageFormat.Png);
                }
                AssistantService service=new AssistantService(root,settings);
                ApplicationInfo app=new ApplicationInfo { windowTitle="製品情報の合成テスト",executableName="SyntheticProduct.exe",WindowHandle=12,processId=34 };
                const string question="この商品のメーカー公式サイトに掲載されている価格を教えてください。";
                Require(!service.DecideSearch(question).Enabled,"no explicit search trigger in test question");
                AnswerResult result=service.AskWithResult(question,png,app,dir,CancellationToken.None);
                Require(!result.Fallback.initialWebSearchCalled,"first attempt never searches");
                Require(result.Fallback.attempted && result.Fallback.outcome=="completed" && result.Fallback.webSearchCalled,"automatic retry actually uses hosted web search");
                Require(result.Answer.Contains("円") && result.Answer.Contains("http"),"final answer contains price and source");
                Require(File.ReadAllText(Path.Combine(dir,"answer.txt"))==result.Answer && !File.Exists(Path.Combine(dir,"codex.events.jsonl")),"explicit debug saving retains the final answer without native event logs");
                report["directory"]=dir; report["fallback"]=result.Fallback; report["answer"]=result.Answer;
                string next=Path.Combine(dir,"next-image-only"); Directory.CreateDirectory(next);
                AnswerResult plain=service.AskWithResult("画面の商品名を教えてください。",png,app,next,CancellationToken.None);
                Require(!plain.Fallback.attempted && !plain.Search.Enabled,"next useful image answer stays offline");
                Require(plain.Answer.Contains("Switch") || plain.Answer.Contains("スイッチ"),"next answer uses image");
                Require(service.BuildPrompt("画面の商品名を教えてください。",app).Contains(result.Answer) && !service.DecideSearch("画面の商品名を教えてください。").Enabled,"past search answer remains in memory without enabling new search");
                report["nextFallback"]=plain.Fallback; report["nextAnswer"]=plain.Answer;
            } else if(args[0]=="--replay-test") {
                Settings legacy=new JavaScriptSerializer().Deserialize<Settings>("{\"PushToTalkKey\":\"F9\",\"CancelKey\":\"F10\"}");
                legacy.Validate(); Require(legacy.ReplayKey=="Alt+R" && legacy.PushToTalkKey=="F9" && legacy.CancelKey=="F10","missing replay key receives new default while explicit legacy shortcuts are retained");
                legacy.ReplayKey="Ctrl+Alt+F8"; legacy.Validate();
                Hotkey shortcut=Hotkey.Parse(legacy.ReplayKey);
                Require(shortcut.Key==119 && shortcut.Ctrl && shortcut.Alt,"custom replay shortcut");
                foreach(string conflict in new[]{"F9","Ctrl+F9","F10","Alt+F10"}) {
                    legacy.ReplayKey=conflict; bool rejected=false;
                    try { legacy.Validate(); } catch(Exception) { rejected=true; }
                    Require(rejected,"overlapping replay shortcut rejected: "+conflict);
                }
                report["checks"]="legacy settings / custom shortcut / conflicting keys; replay uses only the current running session";
            } else if(args[0]=="--capture-fallback-test") {
                string dir=Path.Combine(root,"runtime","capture-fallback-test-work",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                bool captureCalled=false; string disabledPath=Path.Combine(dir,"disabled.png");
                using(OptionalScreenshot disabled=OptionalScreenshot.Take(delegate { captureCalled=true; throw new InvalidOperationException("disabled pixel source must not run"); },CancellationToken.None,"target",false)) {
                    Require(!captureCalled && !disabled.Available && !disabled.Failed && disabled.status=="disabled","OFF skips pixel acquisition without a failure cue condition");
                    Require(disabled.Save(disabledPath)==null && !disabled.attached && !File.Exists(disabledPath) && disabled.status=="disabled" && !disabled.Failed,"OFF creates no image attachment or file and remains distinct from failure");
                    Require(disabled.failureStage=="" && disabled.error=="" && disabled.width==0 && disabled.height==0,"OFF has no capture error or image dimensions");
                }
                foreach(string stage in new[]{"target","capture"}) using(OptionalScreenshot failed=OptionalScreenshot.Take(delegate { throw new InvalidOperationException("diagnostic protected screen"); },CancellationToken.None,stage)) {
                    Require(!failed.Available && failed.Failed && failed.status=="failed" && failed.failureStage==stage,"capture exception becomes optional input: "+stage);
                    Require(failed.Save(Path.Combine(dir,stage+".png"))==null && !failed.attached,"failed image is not attached");
                }
                using(OptionalScreenshot black=OptionalScreenshot.Take(delegate { return new Bitmap(128,72); },CancellationToken.None)) Require(!black.Available && black.error.Contains("黒一色"),"fully black image falls back to voice");
                using(OptionalScreenshot good=OptionalScreenshot.Take(delegate { Bitmap bitmap=new Bitmap(128,72); bitmap.SetPixel(127,71,Color.White); return bitmap; },CancellationToken.None)) {
                    Require(good.Available && !good.Failed,"dark image with a small visible detail is preserved");
                    string path=Path.Combine(dir,"good.png"); Require(good.Save(path)==path && good.attached && File.Exists(path),"successful image is attached");
                }
                string blocked=Path.Combine(dir,"blocked.png"); Directory.CreateDirectory(blocked);
                using(OptionalScreenshot badSave=OptionalScreenshot.Take(delegate { Bitmap bitmap=new Bitmap(16,16); bitmap.SetPixel(1,1,Color.White); return bitmap; },CancellationToken.None)) Require(badSave.Save(blocked)==null && badSave.Failed && badSave.failureStage=="save" && !badSave.attached,"image save failure remains optional");
                using(CancellationTokenSource cancel=new CancellationTokenSource()) {
                    cancel.Cancel(); bool invoked=false,cancelled=false;
                    try { using(OptionalScreenshot ignored=OptionalScreenshot.Take(delegate { invoked=true; return new Bitmap(16,16); },cancel.Token)) {} } catch(OperationCanceledException) { cancelled=true; }
                    Require(cancelled && !invoked,"user cancellation is never treated as capture failure");
                    cancelled=false;
                    try { using(OptionalScreenshot ignored=OptionalScreenshot.Take(delegate { invoked=true; return new Bitmap(16,16); },cancel.Token,"capture",false)) {} } catch(OperationCanceledException) { cancelled=true; }
                    Require(cancelled && !invoked,"OFF still respects cancellation without capturing");
                }
                AssistantService service=new AssistantService(root,settings);
                string prompt=service.BuildPrompt("日本の首都は？",new ApplicationInfo(),false);
                Require(prompt.Contains("画像は添付されていません") && !prompt.Contains("添付画像とアプリ情報は質問開始時"),"prompt explicitly describes missing image");
                var disabledSettings=SettingsStorage.Clone(settings); disabledSettings.CaptureEnabled=false; disabledSettings.WebSearch="disabled";
                string disabledPrompt=new AssistantService(root,disabledSettings).BuildPrompt("日本の首都は？",new ApplicationInfo { windowTitle="Fixture application" },false);
                Require(disabledPrompt.Contains("画像は添付されていません") && disabledPrompt.Contains("今回の質問: 日本の首都は？") && disabledPrompt.Contains("Fixture application"),"OFF preserves voice question and application context");
                Require(!disabledPrompt.Contains("スクリーンショットを取得できず") && !disabledPrompt.Contains("添付画像とアプリ情報は質問開始時"),"OFF does not claim capture failure or an attached image");
                report["checks"]="OFF skips pixel source / no image attachment or file / disabled distinct from failure / target failure / capture failure / black image / small visible detail / saved image / save failure / cancellation ON and OFF / voice-only prompt and application context";
            } else if(args[0]=="--voice-only-test") {
                settings.KeepRequestFiles=true;
                string testRoot=Path.Combine(root,"runtime","voice-only-test-work");
                using(RequestJournal journal=new RequestJournal(testRoot,true,DateTimeOffset.Now)) {
                    string dir=journal.DirectoryPath,wav=Path.Combine(dir,"question.wav"),image;
                    using(SpeechSynthesizer s=Speech.Create(settings)) { s.SetOutputToWaveFile(wav,new SpeechAudioFormatInfo(16000,AudioBitsPerSample.Sixteen,AudioChannel.Mono)); s.Speak("日本の首都はどこですか。"); }
                    using(OptionalScreenshot failed=OptionalScreenshot.Take(delegate { throw new InvalidOperationException("diagnostic protected screen"); },CancellationToken.None)) {
                        image=failed.Save(Path.Combine(journal.TemporaryDirectory,"screen.png")); journal.Data["screenshot"]=failed; journal.Data["imageAttached"]=failed.attached; journal.Save();
                    }
                    AssistantService service=new AssistantService(root,settings);
                    string question=service.Transcribe(wav,CancellationToken.None); report["transcription"]=question;
                    string answer=service.Ask(question,image,new ApplicationInfo { windowTitle="撮影不可のテスト",executableName="SyntheticApp.exe" },dir,CancellationToken.None);
                    Require(answer.Contains("東京"),"voice-only question receives a useful answer after capture failure");
                    var mode=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(dir,"input-mode.json"),Encoding.UTF8));
                    Require(!(bool)mode["imageAttached"] && (string)mode["mode"]=="voice_only" && !File.Exists(Path.Combine(dir,"screen.png")),"Codex request contains no screenshot attachment");
                    using(SpeechSynthesizer s=Speech.Create(settings)) { s.SetOutputToWaveFile(Path.Combine(dir,"answer.wav")); s.Speak(AssistantService.Spoken(answer)); }
                    Require(new FileInfo(Path.Combine(dir,"answer.wav")).Length>1000,"voice-only answer speech generated");
                    journal.Finish("completed",null);
                    report["answer"]=answer; report["directory"]=dir; report["imageAttached"]=false;
                }
            } else if(args[0]=="--speech-text-test") {
                string body="『バイオハザード RE:ベロニカ』のSteam版は2027年発売予定です。2026年11月8日時点で、具体的な発売日はまだ発表されていません。";
                string original=body+" [カプコン公式発表](https://www.capcom.co.jp/ir/news/pdf/260608.pdf)・[Steamストア](https://store.steampowered.com/app/4824610/BIOHAZARD_REVeronica/?l=japanese)";
                string spoken=AssistantService.Spoken(original);
                Require(spoken==body,"reported answer must retain only its complete prose");
                Require(original.Contains("[カプコン公式発表]") && original.Contains("https://"),"original answer retains citations");
                Require(AssistantService.Spoken("本文です。 [公式](https://example.com/A_(B))・[ストア](https://example.org/)")=="本文です。","parentheses in URL and citation separator");
                Require(AssistantService.Spoken("本文です。\n出典：[公式](https://example.com/) / [ストア](https://example.org/)")=="本文です。","empty source heading removed");
                Require(AssistantService.Spoken("本文です。（[公式](https://example.com/)）")=="本文です。","empty citation parentheses removed");
                Require(AssistantService.Spoken("[公式](https://example.com/)・[ストア](https://example.org/)")=="","citation-only answer is silent");
                Require(AssistantService.Spoken("[Enter]を押してください。価格は1,000円です。")=="[Enter]を押してください。価格は1,000円です。","ordinary brackets and numeric prose are retained");
                string profile="この女の子は、ゲーム『ダイヤミック・デイズ』に登場する姫野川コトラ（ひめのがわ ことら）です。幼少期を密林で過ごした、少しはにかみ屋で並外れた怪力の持ち主です。";
                string dated=profile+"2026年11月8日確認。\n\n[Lump of Sugar公式キャラクター紹介](https://www.lumpofsugar.co.jp/product/dd/character/character03.php)・[月幕Galgameのプロフィール](https://www.ymgal.games/ca10636)";
                string clean=AssistantService.Spoken(dated);
                Require(clean==profile,"reported verification date and source links removed without changing the profile");
                foreach(string note in new[]{"確認日：2026年11月8日","情報確認日: 2026-11-08。","最終確認日は2026/11/8です。","2026年11月8日に確認しました。","（2026年11月8日確認）"}) Require(AssistantService.Spoken("本文です。\n"+note)=="本文です。","verification annotation: "+note);
                string datedFacts="発売日は2027年3月10日です。2026年11月8日時点では未発売です。2026年11月8日に公開された情報を確認してください。";
                Require(AssistantService.Spoken(datedFacts)==datedFacts,"release dates, as-of facts and meaningful confirmation instructions remain");
                Require(AssistantService.Spoken("本文です。2026年11月8日確認。続きです。")=="本文です。続きです。","text after verification annotation remains");
                Require(AssistantService.Spoken("確認日：2026年11月8日")=="","verification-only answer is silent");
                string dir=Path.Combine(root,"runtime","speech-text-test-work"); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir,"answer.txt"),dated,new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir,"spoken-answer.txt"),clean,new UTF8Encoding(false));
                using(SpeechSynthesizer s=Speech.Create(settings)) { s.SetOutputToWaveFile(Path.Combine(dir,"answer.wav")); s.Speak(clean); }
                Require(new FileInfo(Path.Combine(dir,"answer.wav")).Length>1000,"speech generated from cleaned text");
                report["spokenText"]=clean; report["preservedDateExample"]=datedFacts;
                report["checks"]="reported examples / original preserved / link removal / verification annotations / release and as-of dates preserved / empty metadata / normal brackets / speech file";
            } else if(args[0]=="--search-trigger-test") {
                string text="# コメント\r\n最新情報\r\n現在は\r\nリアルタイム\r\nC++\r\nLIVE NOW\r\nlive now\r\n\r\n";
                SearchTriggers reader=new SearchTriggers(true,text);
                SearchTriggerSnapshot original=reader.Read(); Require(original.keywords.Length==5,"comments, blanks and normalized duplicates");
                Settings testSettings=new Settings { WebSearch="on-demand",WebSearchTriggersText=text,CodexPath=settings.CodexPath,WhisperModel=settings.WhisperModel };
                AssistantService service=new AssistantService(root,testSettings);
                foreach(string question in new[]{"この製品の最新情報を教えて","価格は現在はいくらですか","リアル タイムの情報を教えて","C++の特徴は？","live NOWの情報は？"}) {
                    SearchDecision found=service.DecideSearch(question);
                    Require(found.effectiveMode=="live" && found.reason=="keyword_opt_in" && found.triggerSettings.source=="config.json","registered trigger: "+question);
                }
                foreach(string question in new[]{"通常の画像について","CCCの特徴は？","検索しないで最新情報を説明して","現在は画像だけで判断して","「最新情報」という表示は何？"}) Require(service.DecideSearch(question).effectiveMode=="disabled","trigger must not override exclusion: "+question);
                ApplicationInfo app=new ApplicationInfo { windowTitle="最新情報",WindowHandle=1,processId=2 };
                service.Remember(app,"最新情報を教えて","SEARCH_HISTORY",true);
                Require(service.BuildPrompt("最新情報を教えて",app).Contains("ユーザー登録キーワード"),"registered trigger requests actual tool use");
                Require(service.BuildPrompt("この画面について",app).Contains("Web検索は無効"),"title and prior question do not trigger search");
                SearchDecision before=service.DecideSearch("最新情報を教えて");
                testSettings.WebSearchTriggersText="価格比較\n";
                Require(service.DecideSearch("最新情報を教えて").effectiveMode=="live","an active request retains its accepted settings until apply");
                service=new AssistantService(root,testSettings);
                Require(service.DecideSearch("最新情報を教えて").effectiveMode=="disabled" && service.DecideSearch("価格比較をお願いします").effectiveMode=="live","applied settings add and remove triggers");
                Require(before.triggerSettings.keywords.Contains("最新情報") && !before.triggerSettings.keywords.Contains("価格比較"),"previous request keeps its exact trigger snapshot");
                testSettings.WebSearchTriggersText="# 空にする\n"; service=new AssistantService(root,testSettings);
                Require(service.DecideSearch("価格比較をお願いします").effectiveMode=="disabled" && service.DecideSearch("ネットで調べて").effectiveMode=="live","clearing triggers retains explicit search commands");
                Require(SearchPolicy.Decide("disabled","最新情報",original).effectiveMode=="disabled","fixed disabled mode rejects keyword");
                SearchTriggerSnapshot disabled=new SearchTriggers(false,text).Read();
                Require(disabled.source=="disabled" && disabled.keywords.Length==0,"OFF disables stored keywords without deleting text");
                Require(new SearchTriggers(true,"").Read().keywords.Length==0,"empty inline text does not restore defaults");
                original.keywords[0]="変更"; Require(reader.Read().keywords[0]=="最新情報","snapshot edits cannot change the active trigger set");
                Require(testSettings.WebSearchTriggersText=="# 空にする\n","parsing does not mutate stored text");
                report["checks"]="inline keywords / literal matching / normalization / negation / quotes / no title or history activation / applied settings / frozen snapshot / empty and OFF / explicit command compatibility";
            } else if(args[0]=="--search-policy-test") {
                string[] yes={"ネットで調べて、この商品の相場を教えて","この商品の相場をネットで調べてください。","ネットでこの商品の価格を調べて","ウェブ検索して教えて","WEB検索してください","インターネットで確認して。","オンラインでも調べて","この商品の相場を検索して。","ネットの情報も使って","ネット上の情報も収集して。","ネット検索を使って。","ネット で 調べて"};
                string[] no={"この商品の相場を教えて","検索ボタンはどれ？","検索している画面を説明して","検索してもいいですか","『ネットで調べて』という表示は何？","ネットで調べて。いや、検索しないで","検索せずに答えて","ネットは使わず画像だけで答えて","この画像だけで判断して","ネットで調べないで","ウェブ検索は不要です","ネット検索なしで","検索する必要はありません","ネットの情報は使わないで","画面の情報のみで教えて"};
                yes=yes.Concat(new[]{"表示されているウィッシュリストをWebから検索して一覧なくから何件がセール中かを判定してください","Webから検索して一覧の件数を教えて","ネットで検索して結果を教えて"}).ToArray();
                foreach(string question in yes) Require(SearchPolicy.Decide("on-demand",question).effectiveMode=="live","voice opt-in: "+question);
                foreach(string question in no) Require(SearchPolicy.Decide("on-demand",question).effectiveMode=="disabled","image-only: "+question);
                Require(SearchPolicy.Decide("disabled",yes[0]).effectiveMode=="disabled","explicit disabled setting stays off");
                Require(SearchPolicy.Decide("live",no[0]).effectiveMode=="live" && SearchPolicy.Decide("cached",no[0]).effectiveMode=="cached","fixed search modes remain supported");
                Require(SearchPolicy.Decide("live","検索しないで").effectiveMode=="disabled","voice opt-out overrides fixed search mode");
                Settings testSettings=new Settings { WebSearch="on-demand",CodexPath=settings.CodexPath,WhisperModel=settings.WhisperModel,KeepRequestFiles=true };
                AssistantService service=new AssistantService(root,testSettings);
                ApplicationInfo app=new ApplicationInfo { windowTitle="ネットで調べて",WindowHandle=44,processId=55 };
                service.Remember(app,"画像の話","IMAGE_HISTORY"); service.Remember(app,"ネットで調べて","EXTERNAL_PRICE_HISTORY",true);
                string live=service.BuildPrompt(yes[0],app),plain=service.BuildPrompt(no[0],app);
                Require(live.Contains("Web検索を利用できます（live）") && live.Contains("IMAGE_HISTORY") && live.Contains("EXTERNAL_PRICE_HISTORY"),"new search question receives recent context");
                Require(plain.Contains("Web検索は無効") && plain.Contains("IMAGE_HISTORY") && plain.Contains("EXTERNAL_PRICE_HISTORY"),"new image question receives recent context without enabling search");
                string followup=service.BuildPrompt("もっと詳しく教えて",app);
                Require(followup.Contains("EXTERNAL_PRICE_HISTORY") && followup.Contains("Web検索は無効"),"explicit continuation can explain an old search answer without enabling new search");
                Require(service.DecideSearch(no[0]).effectiveMode=="disabled" && testSettings.WebSearch=="on-demand","one-off command never mutates settings");
                report["positiveCases"]=yes.Length; report["negativeCases"]=no.Length;
                report["checks"]="explicit opt-in / negative priority / quoted text / ordinary UI wording / default reset / recent context independent of search mode / fixed mode compatibility";
            } else if(args[0]=="--search-live-test" || args[0]=="--search-keyword-live-test") {
                bool keywordTest=args[0]=="--search-keyword-live-test";
                settings.WebSearch="on-demand"; settings.KeepRequestFiles=true;
                string dir=Path.Combine(root,"runtime",keywordTest?"search-keyword-live-test-work":"search-live-test-work",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                if(keywordTest) { settings.WebSearchTriggersEnabled=true; settings.WebSearchTriggersText=SearchTriggers.DefaultText; }
                string wav=Path.Combine(dir,"question.wav"),png=Path.Combine(dir,"screen.png");
                using(SpeechSynthesizer s=Speech.Create(settings)) { s.SetOutputToWaveFile(wav,new SpeechAudioFormatInfo(16000,AudioBitsPerSample.Sixteen,AudioChannel.Mono)); s.Speak(keywordTest?"この商品の最新情報として、メーカー公式の価格を教えてください。":"ネットで調べて、この商品のメーカー公式の価格を教えてください。"); }
                using(Bitmap bitmap=new Bitmap(1000,400)) using(Graphics g=Graphics.FromImage(bitmap)) using(Font font=new Font("Yu Gothic UI",30)) {
                    g.Clear(Color.White); g.DrawString("Nintendo Switch 2\n日本語・国内専用\nメーカー：任天堂",font,Brushes.Black,40,40); bitmap.Save(png,ImageFormat.Png);
                }
                AssistantService service=new AssistantService(root,settings); string question=service.Transcribe(wav,CancellationToken.None);
                report["transcription"]=question; Require(service.DecideSearch(question).effectiveMode=="live","spoken search command recognized");
                if(keywordTest) { SearchDecision found=service.DecideSearch(question); Require(found.reason=="keyword_opt_in" && found.matchedCommand=="最新情報","spoken custom keyword recognized"); report["matchedKeyword"]=found.matchedCommand; }
                ApplicationInfo app=new ApplicationInfo { windowTitle="製品情報の合成テスト",executableName="SyntheticProduct.exe",WindowHandle=12,processId=34 };
                AnswerResult response=service.AskWithResult(question,png,app,dir,CancellationToken.None);
                string answer=response.Answer;
                report["answer"]=answer; report["webSearchCalled"]=response.WebSearchCalled;
                Require(response.WebSearchCalled,"Codex must invoke the hosted web search tool");
                Require(answer.Contains("http") && answer.Contains("円"),"searched answer includes price and a source link");
                report["answer"]=answer; report["webSearchCalled"]=response.WebSearchCalled; report["directory"]=dir;
                using(SpeechSynthesizer s=Speech.Create(settings)) { s.SetOutputToWaveFile(Path.Combine(dir,"answer.wav")); s.Speak(AssistantService.Spoken(answer)); }
                string next=Path.Combine(dir,"next-image-only"); Directory.CreateDirectory(next);
                response=service.AskWithResult("画面の商品名を教えてください。",png,app,next,CancellationToken.None); answer=response.Answer;
                var policy=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(next,"search-policy.json"),Encoding.UTF8));
                Require((string)policy["effectiveMode"]=="disabled" && !response.WebSearchCalled,"next question must disable search without an opt-out phrase");
                Require(answer.IndexOf("Switch",StringComparison.OrdinalIgnoreCase)>=0 || answer.Contains("スイッチ"),"next image-only answer remains grounded");
                report["nextAnswer"]=answer; report["nextSearchMode"]=policy["effectiveMode"];
            } else if(args[0]=="--cue-test") {
                using(CuePlayer cues=new CuePlayer(settings.CueVolume)) {
                    Require(cues.WaitReady(5000),"cue output initialization: "+cues.Error);
                    report["outputDevice"]=cues.DeviceName; report["cueVolume"]=settings.CueVolume;
                    using(WaveRecorder microphone=new WaveRecorder(settings.MicrophoneDevice)) {
                        microphone.Start(); Stopwatch submit=Stopwatch.StartNew(); int first=cues.Play(880);
                        report["submitMilliseconds"]=submit.ElapsedMilliseconds;
                        Require(submit.ElapsedMilliseconds<100,"cue request must not block push-to-talk polling");
                        Stopwatch wait=Stopwatch.StartNew(); while(cues.CompletedId<first && wait.ElapsedMilliseconds<3000) Thread.Sleep(10);
                        Require(cues.CompletedId>=first && cues.Error=="","start cue completed while microphone active: "+cues.Error);
                        byte[] audio=microphone.Stop(); Require(audio.Length>0,"microphone continues during cue playback");
                        report["microphoneTestBytes"]=audio.Length;
                    }
                    int last=cues.Play(660); Stopwatch deadline=Stopwatch.StartNew();
                    while(cues.CompletedId<last && deadline.ElapsedMilliseconds<3000) Thread.Sleep(10);
                    Require(cues.CompletedId>=last && cues.Error=="","end cue completed: "+cues.Error);
                    Thread.Sleep(1200); // No game or other output is needed to keep this stream active.
                    last=cues.Play(880); deadline.Restart();
                    while(cues.CompletedId<last && deadline.ElapsedMilliseconds<3000) Thread.Sleep(10);
                    Require(cues.CompletedId>=last && cues.Error=="","cue after idle completed: "+cues.Error);
                    report["completedCues"]=cues.CompletedId;
                    report["checks"]="default output / nonblocking request / microphone coexistence / start and end cues / cue after idle / device completion";
                }
            } else if(args[0]=="--instructions-test") {
                CustomInstructions provider=new CustomInstructions(true,new Settings().CustomInstructionsText);
                InstructionSnapshot snapshot=provider.Read();
                Require(snapshot.source=="config.json" && snapshot.Text.Contains("180文字") && !snapshot.Text.Contains("読み上げない"),"new custom defaults contain personal style preferences only");
                string first="最初の設定：丁寧に説明してください。",second="変更後の設定：500文字程度で詳しく説明してください。";
                provider=new CustomInstructions(true,first); snapshot=provider.Read();
                Require(snapshot.Text==first && snapshot.source=="config.json" && snapshot.characterCount==first.Length,"inline preferences loaded");
                Settings testSettings=new Settings { CustomInstructionsText=first,CodexPath=settings.CodexPath,WhisperModel=settings.WhisperModel };
                AssistantService service=new AssistantService(root,testSettings);
                ApplicationInfo app=new ApplicationInfo { windowTitle="指示テスト用アプリ",executableName="test.exe" };
                string prompt=service.BuildPrompt("次の操作は？",app);
                Require(prompt.Contains(first) && prompt.Contains(app.windowTitle) && prompt.Contains("次の操作は？"),"preferences coexist with app context and current question");
                testSettings.CustomInstructionsText=second; service=new AssistantService(root,testSettings);
                prompt=service.BuildPrompt("詳しく教えて",app);
                Require(prompt.Contains(second) && !prompt.Contains(first) && !prompt.Contains("180文字"),"applied preferences replace previous instructions without hidden length default");
                Require(snapshot.Text==first,"previous request keeps its instruction snapshot");
                testSettings.CustomInstructionsText=" \r\n"; service=new AssistantService(root,testSettings);
                prompt=service.BuildPrompt("次の質問",app);
                Require(!prompt.Contains(second) && !prompt.Contains("ユーザーのカスタム指示（") && !prompt.Contains("180文字"),"cleared preferences omitted from next prompt");
                Require(testSettings.CustomInstructionsText==" \r\n","runtime trimming does not change saved text");
                testSettings.CustomInstructionsText=second; testSettings.CustomInstructionsEnabled=false; service=new AssistantService(root,testSettings);
                Require(!service.BuildPrompt("次の質問",app).Contains(second) && testSettings.CustomInstructionsText==second,"OFF excludes instructions while preserving their content");
                snapshot=new CustomInstructions(false,second).Read();
                Require(snapshot.source=="disabled" && snapshot.Text=="","OFF disables preferences");
                string metadata=new JavaScriptSerializer().Serialize(snapshot);
                Require(!metadata.Contains("\"Text\"") && metadata.Contains("characterCount"),"instruction log metadata excludes duplicate text");
                testSettings.ApplicationInstructionsText="APP_INSTRUCTION_FIXTURE: 画面の識別情報は必要な場合だけ伝えてください。";
                service=new AssistantService(root,testSettings);
                string appPrompt=service.BuildPrompt("画面の操作を教えて",app);
                string nextPrompt=service.BuildPrompt("別の質問です",app);
                Require(appPrompt.Contains(testSettings.ApplicationInstructionsText) && nextPrompt.Contains(testSettings.ApplicationInstructionsText) && !appPrompt.Contains(second),"application instructions apply to every question even when custom instructions are OFF");
                testSettings.CustomInstructionsEnabled=true; service=new AssistantService(root,testSettings);
                prompt=service.BuildPrompt("次の操作は？",app);
                Require(prompt.Contains(second) && prompt.Contains(testSettings.ApplicationInstructionsText),"application and custom instruction fields coexist");
                testSettings.ApplicationInstructionsText=""; service=new AssistantService(root,testSettings);
                prompt=service.BuildPrompt("次の質問",app);
                Require(prompt.Contains(second) && !prompt.Contains("APP_INSTRUCTION_FIXTURE") && !prompt.Contains(ApplicationInstructions.DefaultText.Trim()),"clearing application instructions does not send placeholder or default text");
                testSettings.ApplicationInstructionsText=ApplicationInstructions.DefaultText; service=new AssistantService(root,testSettings);
                prompt=service.BuildPrompt("次の質問",app);
                Require(prompt.Contains(ApplicationInstructions.DefaultText.Trim()) && prompt.Contains(second) && !prompt.Contains("APP_INSTRUCTION_FIXTURE"),"restoring application defaults keeps personal instructions");
                var keyDefaults=new Settings();
                Require(keyDefaults.PushToTalkKey=="Alt+Q" && keyDefaults.CancelKey=="Alt+E" && keyDefaults.ReplayKey=="Alt+R","fresh installation uses requested Alt shortcuts");
                report["checks"]="inline defaults / applied preferences / prompt integration / immutable snapshot / whitespace and empty / OFF preserves content / metadata / independent application instructions / empty placeholder excluded / application defaults restored / shortcut defaults";
            } else if(args[0]=="--context-test") {
                CaptureTarget actual=CaptureTarget.Current(); actual.App.ResolveDescriptions();
                Require(actual.App.processId>0,"foreground process id");
                report["foregroundApplication"]=actual.App;
                string own=ApplicationInfo.ImagePath((uint)Process.GetCurrentProcess().Id);
                Require(string.Equals(Path.GetFileName(own),Path.GetFileName(typeof(Program).Assembly.Location),StringComparison.OrdinalIgnoreCase),"process executable lookup");
                Require(ApplicationInfo.ImagePath(0)=="","unavailable process fallback");
                Settings testSettings=new Settings { GameName="",RememberLastTurns=2,KeepRequestFiles=true };
                AssistantService service=new AssistantService(root,testSettings);
                ApplicationInfo a=new ApplicationInfo { WindowHandle=101,processId=11,ExecutablePath=@"C:\Games\First.exe",executableName="First.exe",windowTitle="最初のゲーム" };
                ApplicationInfo b=new ApplicationInfo { WindowHandle=202,processId=22,ExecutablePath=@"C:\Apps\Second.exe",executableName="Second.exe",windowTitle="別のアプリ" };
                service.Remember(a,"FIRST_ONLY_QUESTION","FIRST_ONLY_ANSWER");
                string prompt=service.BuildPrompt("現在の質問",b);
                Require(prompt.Contains("Second.exe") && prompt.Contains("別のアプリ") && prompt.Contains("FIRST_ONLY"),"recent context is available across apps");
                service.Remember(b,"SECOND_ONLY_QUESTION","SECOND_ONLY_ANSWER");
                a.windowTitle="最初のゲーム - 別のシーン";
                prompt=service.BuildPrompt("続き",a);
                Require(prompt.Contains("FIRST_ONLY") && prompt.Contains("SECOND_ONLY") && prompt.Contains("別のシーン"),"all retained exchanges survive title update");
                ApplicationInfo anotherWindow=new ApplicationInfo { WindowHandle=303,processId=11,ExecutablePath=a.ExecutablePath,windowTitle=a.windowTitle };
                Require(service.BuildPrompt("別ウィンドウ",anotherWindow).Contains("FIRST_ONLY"),"recent context is available across separate windows");
                ApplicationInfo unknown=new ApplicationInfo { windowTitle="名前だけ分かるウィンドウ" };
                Require(service.BuildPrompt("これは何ですか",unknown).Contains(unknown.windowTitle),"title-only fallback");
                service.Remember(a,"LATEST_TWO","answer"); service.Remember(a,"LATEST_THREE","answer");
                Require(!service.BuildPrompt("続き",a).Contains("FIRST_ONLY"),"global recent history limit");
                string serialized=new JavaScriptSerializer().Serialize(a);
                Require(!serialized.Contains("Games") && !serialized.Contains("ContextKey") && !serialized.Contains("WindowHandle"),"internal paths excluded from prompt and log metadata");
                report["checks"]="foreground metadata / executable lookup / unavailable fallback / recent context across apps and windows / updated title / global history limit";
            } else if(args[0]=="--logging-test") {
                LoggingDiagnostics.Run(root,report);
            } else if(args[0]=="--transcription-storage-test") {
                settings.SpeechVolume=85;
                string fixture=Path.Combine(Path.GetTempPath(),"Minus700K-transcription-"+Guid.NewGuid().ToString("N"),"利用者 Ω тест 🐱","Logs","Requests");
                foreach(bool keep in new[]{true,false}) {
                    settings.KeepRequestFiles=keep; string directory;
                    using(var journal=new RequestJournal(fixture,keep,DateTimeOffset.Now)) {
                        directory=journal.DirectoryPath; string wav=Path.Combine(directory,"録音 тест 🐱.wav");
                        using(SpeechSynthesizer s=Speech.Create(settings)) { s.SetOutputToWaveFile(wav,new SpeechAudioFormatInfo(16000,AudioBitsPerSample.Sixteen,AudioChannel.Mono)); s.Speak("日本の首都は東京です。"); }
                        string question=new AssistantService(root,settings).Transcribe(wav,CancellationToken.None);
                        Require(question.Contains("東京"),"recognition succeeds from a user data path outside the application, including non-ANSI characters");
                        if(keep) Require(File.ReadAllText(Path.Combine(directory,"transcript.txt"),Encoding.UTF8).Contains("東京") && File.Exists(Path.Combine(directory,"whisper.stderr.log")),"raw transcription and recognition diagnostics remain when saving is ON");
                        else Require(!File.Exists(Path.Combine(directory,"transcript.txt")),"saving OFF does not write a raw transcript copy");
                        journal.Finish("completed",null);
                    }
                    Require(Directory.Exists(directory)==keep,"recording cleanup respects the debug save toggle");
                }
                report["checks"]="real local recognition / Unicode user paths / binary stdin and UTF-8 stdout / diagnostic save ON and OFF / temporary cleanup; no capture, recording, or AI request";
                report["fixtureDirectory"]=fixture;
            } else if(args[0]=="--self-test") {
                string[] words={ "simple", "日本語の パス", "", "a\"b", "C:\\folder with space\\", "$(nothing); `literal`" };
                int count; IntPtr ptr=CommandLineToArgvW("program.exe "+string.Join(" ",words.Select(HiddenProcess.Quote).ToArray()),out count);
                try { Require(count==words.Length+1,"argument count"); for(int i=0;i<words.Length;i++) Require(Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr,(i+1)*IntPtr.Size))==words[i],"argument escaping "+i); } finally { LocalFree(ptr); }
                Require(!WaveRecorder.Audible(new byte[32000]),"silence rejected");
                Require(Hotkey.Parse("Ctrl+Alt+F9").Key==120,"hotkey parser");
                using(CancellationTokenSource cancel=new CancellationTokenSource(200)) {
                    bool cancelled=false; Stopwatch t=Stopwatch.StartNew();
                    try { HiddenProcess.Run(System.Reflection.Assembly.GetExecutingAssembly().Location,new[]{"--child-wait"},root,null,20,cancel.Token); } catch(OperationCanceledException) { cancelled=true; }
                    Require(cancelled && t.ElapsedMilliseconds<4000,"process cancellation"); report["cancellationMs"]=t.ElapsedMilliseconds;
                }
                ProcessResult result=HiddenProcess.Run(System.Reflection.Assembly.GetExecutingAssembly().Location,new[]{"--child-exit"},root,null,5,CancellationToken.None);
                Require(result.ExitCode==7,"exit code propagation");
                byte[] binary=new byte[1024*1024]; for(int i=0;i<binary.Length;i++) binary[i]=(byte)i;
                result=HiddenProcess.Run(System.Reflection.Assembly.GetExecutingAssembly().Location,new[]{"--child-input-hash"},root,null,10,CancellationToken.None,binary);
                using(var hash=System.Security.Cryptography.SHA256.Create()) Require(result.ExitCode==0 && result.Output.Trim()==BitConverter.ToString(hash.ComputeHash(binary)),"binary stdin transfers every byte including NUL and Ctrl-Z");
                using(CancellationTokenSource cancel=new CancellationTokenSource(200)) {
                    bool cancelled=false; Stopwatch t=Stopwatch.StartNew();
                    try { HiddenProcess.Run(System.Reflection.Assembly.GetExecutingAssembly().Location,new[]{"--child-wait"},root,null,20,cancel.Token,binary); } catch(OperationCanceledException) { cancelled=true; }
                    Require(cancelled && t.ElapsedMilliseconds<4000,"cancel while a child leaves the binary input pipe blocked");
                }
                bool timedOut=false; Stopwatch timeout=Stopwatch.StartNew();
                try { HiddenProcess.Run(System.Reflection.Assembly.GetExecutingAssembly().Location,new[]{"--child-wait"},root,null,1,CancellationToken.None,binary); } catch(TimeoutException) { timedOut=true; }
                Require(timedOut && timeout.ElapsedMilliseconds<4000,"timeout covers a blocked binary input pipe");
                using(WaveRecorder r=new WaveRecorder(settings.MicrophoneDevice)) { r.Start(); Thread.Sleep(300); byte[] a=r.Stop(); Thread.Sleep(150); byte[] b=r.Stop(); Require(a.Length>0 && a.Length==b.Length,"capture stops on release"); report["microphoneTestBytes"]=a.Length; }
                report["checks"]="argument escaping / hotkeys / silence / cancellation / child exit / microphone stop";
            } else if(args[0]=="--capture-test" || args[0]=="--game-test") {
                CaptureTarget target=CaptureTarget.Current(); string path=Path.Combine(root,"runtime","capture-test.png");
                target.App.ResolveDescriptions(); report["target"]=target.Title; report["application"]=target.App;
                report["captureBoundsSource"]=target.BoundsSource;
                report["captureBounds"]=new { x=target.Bounds.X,y=target.Bounds.Y,width=target.Bounds.Width,height=target.Bounds.Height };
                using(DesktopCapture capture=new DesktopCapture()) using(Bitmap bitmap=capture.Capture(target,settings.CaptureMaximumWidth)) {
                    bitmap.Save(path,ImageFormat.Png); report["width"]=bitmap.Width; report["height"]=bitmap.Height;
                    var colors=new HashSet<int>(); for(int y=0;y<bitmap.Height;y+=17) for(int x=0;x<bitmap.Width;x+=17) colors.Add(bitmap.GetPixel(x,y).ToArgb());
                    Require(colors.Count>4,"capture appears blank"); report["sampledColors"]=colors.Count;
                    using(Bitmap again=capture.Capture(target,settings.CaptureMaximumWidth)) Require(again.Width==bitmap.Width,"repeat capture");
                    report["skippedPointerOnlyFrames"]=capture.SkippedPointerOnlyFrames;
                    report["lastPresentTime"]=capture.LastPresentTime;
                }
                report["target"]=target.Title;
                if(args[0]=="--game-test") {
                    string dir=Path.Combine(root,"runtime","game-test-work"); Directory.CreateDirectory(dir);
                    AssistantService service=new AssistantService(root,settings);
                    string answer=service.Ask("動作確認です。添付した画面に何が表示されているか、一文だけで教えてください。アプリの操作はしないでください。",path,target.App,dir,CancellationToken.None);
                    report["answer"]=answer;
                    using(SpeechSynthesizer s=Speech.Create(settings)) s.Speak(AssistantService.Spoken(answer));
                }
            } else if(args[0]=="--pipeline-test" || args[0]=="--application-test") {
                bool applicationTest=args[0]=="--application-test";
                string dir=Path.Combine(root,"runtime",applicationTest?"application-test-work":"pipeline-test-work"); Directory.CreateDirectory(dir);
                string wav=Path.Combine(dir,"question.wav"),png=Path.Combine(dir,"screen.png");
                using(SpeechSynthesizer s=Speech.Create(settings)) { s.SetOutputToWaveFile(wav,new SpeechAudioFormatInfo(16000,AudioBitsPerSample.Sixteen,AudioChannel.Mono)); s.Speak(applicationTest?"このエラーを解消するには、どのボタンを押せばよいですか。":"この画面に大きく表示されている数字を教えてください。"); }
                if(applicationTest) {
                    // Synthetic ordinary-app screen: no real document is sent and no UI is opened.
                    using(Bitmap b=new Bitmap(1000,600)) using(Graphics g=Graphics.FromImage(b)) using(Font f=new Font("Yu Gothic UI",22)) using(Font small=new Font("Yu Gothic UI",16)) {
                        g.Clear(Color.WhiteSmoke); g.FillRectangle(Brushes.SlateGray,0,0,1000,48);
                        g.DrawString("作業メモ — 保存設定",small,Brushes.White,18,10);
                        g.DrawString("ファイル    編集    表示    ヘルプ",small,Brushes.Black,18,58);
                        g.DrawString("保存先の設定",f,Brushes.Black,42,126);
                        g.DrawString("エラー：保存先フォルダーが設定されていません。",f,Brushes.DarkRed,42,212);
                        g.FillRectangle(Brushes.RoyalBlue,44,325,330,66);
                        g.DrawString("保存先を選択",f,Brushes.White,90,337);
                        g.DrawRectangle(Pens.Gray,420,325,230,66); g.DrawString("キャンセル",f,Brushes.Black,456,337);
                        b.Save(png,ImageFormat.Png);
                    }
                } else using(Bitmap b=new Bitmap(800,450)) using(Graphics g=Graphics.FromImage(b)) using(Font f=new Font("Arial",110,FontStyle.Bold)) { g.Clear(Color.FromArgb(18,28,48)); g.DrawString("7312",f,Brushes.White,new PointF(180,120)); b.Save(png,ImageFormat.Png); }
                AssistantService service=new AssistantService(root,settings); Stopwatch t=Stopwatch.StartNew(); string question=service.Transcribe(wav,CancellationToken.None); report["transcriptionMs"]=t.ElapsedMilliseconds; report["transcription"]=question;
                Require(question.Contains(applicationTest?"ボタン":"数字"),"Japanese transcription failed"); t.Restart();
                ApplicationInfo synthetic=new ApplicationInfo { windowTitle=applicationTest?"作業メモ — 保存設定":"Minus700K synthetic test",executableName=applicationTest?"WorkNotes.exe":"Minus700K.exe",processId=(uint)Process.GetCurrentProcess().Id };
                string answer=service.Ask(question,png,synthetic,dir,CancellationToken.None); report["codexMs"]=t.ElapsedMilliseconds; report["answer"]=answer;
                Require(applicationTest?answer.Contains("保存先を選択"):answer.Replace(",","").Contains("7312"),"image grounding failed");
                if(settings.KeepRequestFiles) {
                    var metadata=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(dir,"custom-instructions.json"),Encoding.UTF8));
                    Require(metadata.ContainsKey("characterCount") && !metadata.ContainsKey("Text"),"instruction diagnostics exclude the instruction text");
                    Require(!File.Exists(Path.Combine(dir,"custom-instructions.txt")) && !File.Exists(Path.Combine(dir,"prompt.txt")),"no duplicate instructions or prompt logs");
                    report["customInstructionsSource"]=metadata["source"];
                }
                string outWav=Path.Combine(dir,"answer.wav"); using(SpeechSynthesizer s=Speech.Create(settings)) { s.SetOutputToWaveFile(outWav); s.Speak(AssistantService.Spoken(answer)); }
                Require(new FileInfo(outWav).Length>1000,"Japanese speech output failed"); report["speechBytes"]=new FileInfo(outWav).Length;
            } else if(args[0]=="--speak-test") { using(SpeechSynthesizer s=Speech.Create(settings)) s.Speak("画面音声アシスタントの音声確認です。"); }
            else throw new Exception("Unknown diagnostic mode.");
            report["passed"]=true;
        } catch(Exception e) { report["passed"]=false; report["error"]=e.ToString(); Log.Write("diagnostics "+e); }
        finally { Volatile.Write(ref monitor,false); observer.Join(1000); report["foregroundUnchanged"]=Native.GetForegroundWindow()==initial; report["foregroundMismatchSamples"]=focusChanges; report["elapsedMs"]=total.ElapsedMilliseconds; File.WriteAllText(reportFile,new JavaScriptSerializer().Serialize(report),new UTF8Encoding(false)); }
        return (bool)report["passed"]?0:1;
    }
}
}
