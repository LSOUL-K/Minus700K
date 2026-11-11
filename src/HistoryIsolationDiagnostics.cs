using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace Minus700K {
internal static class HistoryIsolationDiagnostics {
    static void Require(bool condition,string description) { if(!condition) throw new Exception(description); }
    internal static void Run(string root,Settings settings,Dictionary<string,object> report,bool live) {
        settings.KeepRequestFiles=false; settings.RememberLastTurns=3;
        settings.WebSearch="on-demand"; settings.AutoWebSearchFallback=true; settings.GameName="";
        var service=new AssistantService(root,settings);
        var a=new ApplicationInfo { WindowHandle=101,processId=11,windowTitle="合成ブラウザ画面",executableName="FixtureBrowser.exe" };
        var b=new ApplicationInfo { WindowHandle=202,processId=22,windowTitle="別の合成アプリ",executableName="FixtureApp.exe" };
        service.Remember(a,"信号機とは何ですか","TRAFFIC_ANSWER");
        string[] independent={"表示されているウィッシュリストをWebから検索して一覧なくから何件がセール中かを判定してください","この画面を説明して","この商品の相場を教えて","以前発売されたこのゲームの名前は？","直前に押したボタンは何ですか","これは何ですか"};
        foreach(string question in independent) Require(service.BuildPrompt(question,a).Contains("TRAFFIC_ANSWER"),"every question receives recent context for the model to judge relevance: "+question);
        Require(service.DecideSearch(independent[0]).Enabled,"reported Web search phrase enables search");
        Require(service.BuildPrompt("もっと詳しく教えて",a).Contains("TRAFFIC_ANSWER"),"clear continuation preserves useful context");
        service.Remember(a,"この一覧で割引されている商品は何件ですか","WISHLIST_ANSWER");
        string continuation=service.BuildPrompt("もっと詳しく教えて",a);
        Require(continuation.Contains("WISHLIST_ANSWER") && continuation.Contains("TRAFFIC_ANSWER"),"a new topic retains both exchanges within the memory limit");
        service.Remember(b,"別の話を教えて","LATEST_OTHER_APP");
        string confirmation=service.BuildPrompt("はい、そうです",a);
        Require(confirmation.Contains("LATEST_OTHER_APP") && confirmation.Contains("WISHLIST_ANSWER") && confirmation.Contains("TRAFFIC_ANSWER"),"confirmation receives all retained exchanges even after app switch");
        Require(service.BuildPrompt("はい、ところでこの画面の商品名は？",a).Contains("LATEST_OTHER_APP"),"topic-changing prefix does not remove recent context");
        string optOutPrompt=service.BuildPrompt("以前の質問は使わないで、この画面を説明して",a);
        Require(optOutPrompt.Contains("WISHLIST_ANSWER") && optOutPrompt.Contains("以前の質問は使わないで、この画面を説明して"),"model receives the current instruction to ignore supplied context");
        Require(service.BuildPrompt("以前聞いたゲームの難易度の質問を教えて",a).Contains("LATEST_OTHER_APP"),"topic recall receives actual retained context");
        Require(service.BuildPrompt("2つ前の質問について教えて",a).Contains("WISHLIST_ANSWER"),"ordinal recall receives actual retained context");
        report["checks"]="automatic context for every wording / context across topics and apps / current opt-out instruction / topic and ordinal references use memory / reported Web phrase";
        if(!live) return;
        string fixture=Path.Combine(root,"runtime","history-isolation-live-test-work",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture); service.ClearHistory();
        AnswerResult traffic,wishlist,confirmed;
        using(var journal=new RequestJournal(fixture,false,DateTimeOffset.Now)) traffic=service.AskWithResult("信号機とは何ですか",null,a,journal.DirectoryPath,CancellationToken.None);
        using(var journal=new RequestJournal(fixture,false,DateTimeOffset.Now)) {
            string png=Path.Combine(journal.TemporaryDirectory,"screen.png"); DrawList(png);
            wishlist=service.AskWithResult("この一覧で割引されている商品は何件ですか。",png,a,journal.DirectoryPath,CancellationToken.None);
        }
        Require(traffic.ThreadId!=wishlist.ThreadId,"requests use distinct ephemeral Codex sessions");
        Require(wishlist.RecentMemoryCount==1,"the next ephemeral request receives app-managed memory");
        Require(Regex.IsMatch(wishlist.Answer,@"[2２二]\s*(?:件|つ|個|商品)"),"new screenshot question is answered directly with two sale items: "+wishlist.Answer);
        Require(!Regex.IsMatch(wishlist.Answer,@"信号|話題|切り替え|よいですか|合っていますか"),"no unrelated topic confirmation");
        service.Remember(a,"このことわざの意味を教えて。","『光陰矢の如し』の意味を説明すればよいですか？");
        using(var journal=new RequestJournal(fixture,false,DateTimeOffset.Now)) {
            string png=Path.Combine(journal.TemporaryDirectory,"screen.png"); DrawList(png);
            confirmed=service.AskWithResult("はいそうです",png,b,journal.DirectoryPath,CancellationToken.None);
        }
        Require(confirmed.RecentMemoryCount==3,"memory remains bounded across ephemeral requests and app switches");
        Require(Regex.IsMatch(confirmed.Answer,@"光陰|月日|歳月|時間") && !Regex.IsMatch(confirmed.Answer,@"セール|割引|よいですか|合っていますか"),"confirmation uses actual previous reply instead of current screen: "+confirmed.Answer);
        Require(Directory.GetFiles(fixture,"*",SearchOption.AllDirectories).Length==0,"non-debug requests leave no retained request files");
        report["newTopicAnswer"]=wishlist.Answer; report["confirmationAnswer"]=confirmed.Answer; report["fixtureDirectory"]=fixture;
    }
    static void DrawList(string path) {
        using(var bitmap=new Bitmap(1000,440)) using(Graphics graphics=Graphics.FromImage(bitmap)) using(var font=new Font("Arial",32)) {
            graphics.Clear(Color.White);
            graphics.DrawString("WISHLIST (3 items)\nAster      -50%      500 JPY\nBirch                  1000 JPY\nCedar     -20%      800 JPY",font,Brushes.Black,30,25);
            bitmap.Save(path,ImageFormat.Png);
        }
    }
}
}
