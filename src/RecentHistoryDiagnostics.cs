using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Minus700K {
// Synthetic prompts test context delivery without credentials or AI requests.
internal static class RecentHistoryDiagnostics {
    static int checks;
    static readonly BindingFlags PrivateInstance=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool value,string description) { checks++; if(!value) throw new Exception(description); }
    static List<Turn> Memory(AssistantService service) { return (List<Turn>)typeof(AssistantService).GetField("history",PrivateInstance).GetValue(service); }
    static RecentTurn[] Snapshot(AssistantService service) {
        return (RecentTurn[])typeof(AssistantService).GetMethod("CaptureRecentHistory",PrivateInstance).Invoke(service,new object[]{CancellationToken.None});
    }
    static Settings Options(Settings original,string fixture,int count=3,string language="ja") {
        var settings=SettingsStorage.Clone(original);
        settings.RememberLastTurns=count; settings.KeepRequestFiles=false; settings.AppLanguage=language; settings.Language=language;
        settings.WhisperModel="model.bin"; settings.CodexPath=Path.Combine(fixture,"fixture-codex-must-not-run.exe");
        settings.CustomInstructionsEnabled=false; settings.GameName=""; return settings;
    }
    static void CompleteContext(string prompt,string description) {
        Check(new[]{"SECOND_QUESTION","SECOND_ANSWER","THIRD_QUESTION","THIRD_ANSWER","FOURTH_QUESTION","FOURTH_ANSWER"}.All(prompt.Contains),description);
        Check(!prompt.Contains("EVICTED_SENTINEL") && !prompt.Contains("PERSISTED_ONLY_SENTINEL"),"only bounded memory is supplied");
        Check(prompt.IndexOf("SECOND_QUESTION",StringComparison.Ordinal)<prompt.IndexOf("THIRD_QUESTION",StringComparison.Ordinal)
            && prompt.IndexOf("THIRD_QUESTION",StringComparison.Ordinal)<prompt.IndexOf("FOURTH_QUESTION",StringComparison.Ordinal),"memory is ordered oldest to newest");
    }
    internal static void Run(string root,Settings original,Dictionary<string,object> report) {
        checks=0;
        string fixture=Path.Combine(root,"runtime","recent-history-test-work",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture,"vendor","whisper"));
        File.WriteAllText(Path.Combine(fixture,"vendor","whisper","whisper-cli.exe"),"fixture-must-not-run");
        File.WriteAllText(Path.Combine(fixture,"model.bin"),"fixture-must-not-load");
        File.WriteAllText(Path.Combine(fixture,"fixture-codex-must-not-run.exe"),"fixture-must-not-run");
        string legacy=Path.Combine(fixture,"runtime","codex-history"); Directory.CreateDirectory(legacy);
        string legacyFile=Path.Combine(legacy,"20990101_000000_000_old.json");
        File.WriteAllText(legacyFile,"{\"schemaVersion\":2,\"threadId\":\"PERSISTED_ONLY_SENTINEL\",\"storage\":\"private\"}");
        string oldRequest=Path.Combine(fixture,"runtime","requests","old"); Directory.CreateDirectory(oldRequest);
        File.WriteAllText(Path.Combine(oldRequest,"answer.txt"),"PERSISTED_ONLY_SENTINEL");
        string[] files=Directory.GetFiles(fixture,"*",SearchOption.AllDirectories); byte[][] before=files.Select(File.ReadAllBytes).ToArray();
        var a=new ApplicationInfo { WindowHandle=101,processId=11,windowTitle="Window A",executableName="MemoryA.exe" };
        var b=new ApplicationInfo { WindowHandle=202,processId=22,windowTitle="Window B",executableName="MemoryB.exe" };
        var c=new ApplicationInfo { WindowHandle=303,processId=33,windowTitle="Window C",executableName="MemoryC.exe" };
        string[] queries={"先ほどの内容を教えて","あの件だけど、別の方法はある？","なるほど、ではどうすればいい？","この画面を説明して",
            "もっと詳しく教えて","はいそうです","直前の質問を教えて","3つ前の質問を教えて","以前聞いた保存済みの質問を教えて",
            "以前の質問は使わないで、今回の質問だけ答えて","What about the other option?","Ignore past conversations. What is this screen?"};
        // Even unreadable legacy indexes and transcript files must be irrelevant.
        using(File.Open(legacyFile,FileMode.Open,FileAccess.Read,FileShare.None))
        using(File.Open(Path.Combine(oldRequest,"answer.txt"),FileMode.Open,FileAccess.Read,FileShare.None)) {
            foreach(bool debug in new[]{false,true}) foreach(string language in new[]{"ja","en"}) {
                var options=Options(original,fixture,3,language); options.KeepRequestFiles=debug;
                var service=new AssistantService(fixture,options);
                service.Remember(a,"OLD_QUESTION","EVICTED_SENTINEL");
                service.Remember(a,"SECOND_QUESTION","SECOND_ANSWER");
                service.Remember(b,"THIRD_QUESTION","THIRD_ANSWER",true);
                service.Remember(c,"FOURTH_QUESTION","FOURTH_ANSWER");
                Memory(service).Last().RecordedUtc=DateTime.UtcNow.AddHours(-2);
                foreach(string question in queries) {
                    string prompt=service.BuildPrompt(question,a,false);
                    CompleteContext(prompt,"every wording gets all retained topics, windows and ages: "+question);
                    Check(prompt.Contains(question),"the current request, including instructions to ignore context, reaches the AI");
                }
                var snapshot=Snapshot(service);
                Check(snapshot.Select(t=>t.questionsAgo).SequenceEqual(new[]{3,2,1}),"ordinal metadata counts completed exchanges globally");
                Check(snapshot.All(t=>t.source=="memory") && snapshot[1].usedWeb && snapshot[1].application=="MemoryB.exe","context includes source, original app and historical web-use metadata");
                service.Remember(a,"NEW_QUESTION","NEW_ANSWER");
                Check(snapshot[0].answer=="SECOND_ANSWER" && snapshot[2].answer=="FOURTH_ANSWER","prepared requests and retries keep a consistent memory snapshot");
                Check(Memory(service).Count==3 && Memory(service).All(t=>t.Answer!="SECOND_ANSWER"),"a new exchange evicts the globally oldest item");
                snapshot[1].answer="MUTATED_SNAPSHOT";
                Check(service.BuildPrompt("new request",a,false).Contains("THIRD_ANSWER"),"a request snapshot cannot mutate retained memory");
            }
            foreach(int limit in Enumerable.Range(0,11)) {
                var bounded=new AssistantService(fixture,Options(original,fixture,limit));
                for(int i=0;i<12;i++) bounded.Remember(i%2==0?a:b,"QUESTION_"+i,"ANSWER_"+i+"_END");
                string prompt=bounded.BuildPrompt("何か別の案はある？",c,false);
                Check(Memory(bounded).Count==limit,"retention honors limit "+limit);
                for(int i=0;i<12;i++) Check(prompt.Contains("ANSWER_"+i+"_END")==(i>=12-limit),"transmission honors limit "+limit+", exchange "+i);
            }
            var three=new AssistantService(fixture,Options(original,fixture));
            three.Remember(a,"SECOND_QUESTION","SECOND_ANSWER"); three.Remember(b,"THIRD_QUESTION","THIRD_ANSWER"); three.Remember(c,"FOURTH_QUESTION","FOURTH_ANSWER");
            CompleteContext(three.BuildPrompt("4つ前の質問を教えて",a,false),"an out-of-range request supplies actual retained context without inventing an older exchange");
            var source=new AssistantService(fixture,Options(original,fixture));
            source.UseHistoryAccount("fixture-account"); source.Remember(a,"old","TRANSFER_OLD_SENTINEL"); source.Remember(b,"new","TRANSFER_NEW_SENTINEL");
            var reduced=new AssistantService(fixture,Options(original,fixture,1)); reduced.TransferRecentHistoryFrom(source);
            string transferred=reduced.BuildPrompt("全く別の言い方",c,false);
            Check(Memory(source).Count==0 && Memory(reduced).Count==1 && transferred.Contains("TRANSFER_NEW_SENTINEL") && !transferred.Contains("TRANSFER_OLD_SENTINEL"),"settings apply transfers and immediately bounds context without a wording gate");
            reduced.UseHistoryAccount("fixture-account"); Check(Memory(reduced).Count==1,"settings transfer preserves the account boundary");
            reduced.TransferRecentHistoryFrom(reduced); Check(Memory(reduced).Count==1,"self-transfer retains memory");
            var zero=new AssistantService(fixture,Options(original,fixture,0)); zero.TransferRecentHistoryFrom(reduced);
            var increased=new AssistantService(fixture,Options(original,fixture,3)); increased.TransferRecentHistoryFrom(zero);
            Check(Memory(reduced).Count==0 && Memory(zero).Count==0 && Memory(increased).Count==0,"zero clears memory and increasing the limit cannot restore it");
            increased.Remember(a,"account question","ACCOUNT_SENTINEL"); increased.UseHistoryAccount("another-account");
            Check(Memory(increased).Count==0,"account switching clears previous-account memory");
            three.ClearHistory(); Check(!three.BuildPrompt("先ほどの内容を教えて",a,false).Contains("FOURTH_ANSWER"),"cleared memory is no longer transmitted");
            var restarted=new AssistantService(fixture,Options(original,fixture));
            Check(Memory(restarted).Count==0 && !restarted.BuildPrompt("先ほどの内容を教えて",a,false).Contains("PERSISTED_ONLY_SENTINEL"),"restart restores nothing from disk");
            CheckRetry(fixture,Options(original,fixture),a);
        }
        Check(files.OrderBy(p=>p).SequenceEqual(Directory.GetFiles(fixture,"*",SearchOption.AllDirectories).OrderBy(p=>p)),"memory creates no conversation files");
        for(int i=0;i<files.Length;i++) Check(before[i].SequenceEqual(File.ReadAllBytes(files[i])),"legacy data and fixture files remain unchanged");
        report["checkCount"]=checks; report["fixtureDirectory"]=fixture;
        report["checks"]="every request / bounded chronological context / all windows, topics, ages, wordings and languages / all limits 0 through 10 / stable retry snapshot / no legacy reads or conversation persistence / settings transfer / account boundary / clear and restart";
    }
    static void CheckRetry(string root,Settings settings,ApplicationInfo app) {
        settings.WebSearch="on-demand"; settings.AutoWebSearchFallback=true;
        var service=new AssistantService(root,settings); service.Remember(app,"earlier question","RETRY_MEMORY_SENTINEL");
        RecentTurn[] prepared=Snapshot(service);
        MethodInfo build=typeof(AssistantService).GetMethods(PrivateInstance).Single(m=>m.Name=="BuildPrompt" && m.GetParameters().Length==6);
        int attempts=0;
        var result=SearchFallback.Run(settings,SearchPolicy.Decide("on-demand","この商品の価格は？"),delegate(SearchDecision search,bool assess) {
            attempts++;
            string prompt=(string)build.Invoke(service,new object[]{"この商品の価格は？",app,new InstructionSnapshot { Text="" },search,false,prepared});
            Check(prompt.Contains("RETRY_MEMORY_SENTINEL") && !prompt.Contains("LATER_MEMORY_SENTINEL"),"both search attempts use the same prepared memory snapshot");
            if(attempts==1) {
                service.ClearHistory(); service.Remember(app,"later question","LATER_MEMORY_SENTINEL");
                return new AnswerDraft { answer="Need current price",status="needs_web",reason="price unavailable" };
            }
            return new AnswerDraft { answer="Final price",status="answered",reason="fixture",WebSearchCalled=true };
        },CancellationToken.None);
        Check(attempts==2 && result.Fallback.attempted && result.Answer=="Final price","automatic search retry preserves context without a saved lookup");
    }
}
}
