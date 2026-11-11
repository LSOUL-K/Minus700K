using System;
using System.Collections.Generic;

namespace Minus700K {
internal static class EnglishBehaviorDiagnostics {
    static void Check(bool condition,string description) { if(!condition) throw new Exception("English behavior: "+description); }
    internal static void Run(Dictionary<string,object> report) {
        var en=LanguageBehavior.For("en");
        string[] positive={"Search the web for this product.","Please search online.","Could you search for the release date?","Look it up.","Please look up this model.","Can you check the price online?","What is this? Search the internet.","Use the web to find the answer.","Browse the internet for details.","Find the latest price online.","I want you to search the web.","SEARCH THE WEB","Search   online.","Please also search the web.","Look this up online.","Could you look that up?"};
        foreach(string question in positive) Check(SearchPolicy.Decide("on-demand",question,null,en).reason=="voice_opt_in","search request: "+question);
        string[] negative={"Don't search the web.","Do not search online.","Don’t use the internet.","Never browse the web.","Answer without searching.","Avoid using the web.","No web search, please.","Turn off online search.","Use only the screenshot.","Answer based on the screen only.","Search the web, but do not search online.","Search online. Ignore that, don't use the web.","Don't look this up on the web.","Do not perform a web search.","Please do not do a web search.","No search please","Avoid conducting an online search.","Search online. No search please."};
        foreach(string question in negative) {
            Check(SearchPolicy.Decide("live",question,null,en).reason=="voice_opt_out","search refusal overrides configured live: "+question);
            Check(SearchPolicy.Decide("on-demand",question,new SearchTriggerSnapshot { keywords=new[]{"web","online"} },en).reason=="voice_opt_out","refusal overrides keywords: "+question);
        }
        string[] independent={"Where is the search button?","What does 'search online' mean?","Explain \"search the web\".","The message says ‘search online’.","Explain 'don't search the web'.","I am searching online.","What is a web search?","How does the search field work?","There is no search button.","What does 'look this up online' mean?"};
        foreach(string question in independent) Check(SearchPolicy.Decide("on-demand",question,null,en).reason=="default_image_only","descriptive or quoted text is not a command: "+question);
        var triggers=new SearchTriggerSnapshot { keywords=new[]{"web","real-time","price comparison"} };
        Check(SearchPolicy.Decide("on-demand","Explain my webcam.",triggers,en).reason=="default_image_only","web keyword cannot match webcam");
        Check(SearchPolicy.Decide("on-demand","Explain the web feature.",triggers,en).reason=="keyword_opt_in","word keyword still matches");
        Check(SearchPolicy.Decide("on-demand","PRICE   COMPARISON please.",triggers,en).reason=="keyword_opt_in","phrase keyword normalizes case and whitespace");
        Check(SearchPolicy.Decide("disabled",positive[0],triggers,en).effectiveMode=="disabled","disabled mode remains disabled");
        Check(new SearchTriggers(true,"price comparison\nPRICE   COMPARISON\npricecomparison","en",en).Read().keywords.Length==2,"English keyword deduplication retains word boundaries");

        Check(en.CleanTranscription("[Noise] (MUSIC) What is this? (applause)")=="What is this?","English transcription annotations");
        Check(en.CleanTranscription("What does (optional) mean?")=="What does (optional) mean?","ordinary parenthetical speech remains");

        foreach(string note in new[]{"Checked: 2026-11-10","Verified: 2026/11/10.","Last checked: November 10, 2026","(Date checked: 10 November 2026)"}) Check(AssistantService.Spoken("It is available.\n"+note,"en")=="It is available.","verification metadata: "+note);
        Check(AssistantService.Spoken("It is available.\nSources: [Store](https://example.com)","ja")=="It is available.","English sources removed after switching to Japanese");
        string facts="It was released on November 10, 2026. As of 2026-11-10, it costs $20. Check the date before ordering.";
        Check(AssistantService.Spoken(facts,"en")==facts,"useful dates and instructions are preserved");
        Check(LanguageBehavior.ForInput(new Settings { AppLanguage="en",Language="ja" }).Code=="ja" && LanguageBehavior.ForInput(new Settings { AppLanguage="en",Language="auto" }).Code=="en","input and UI languages remain independent");
        Check(LanguageBehavior.For("zh").Code=="ja" && LanguageBehavior.VoiceLanguage("en")=="en","unsupported input falls back without registering Chinese");
        Check(SearchPolicy.Decide("live","検索しないで",null,LanguageBehavior.For("ja")).reason=="voice_opt_out","Japanese recognition regression");
        report["englishBehaviorChecks"]="imperatives / explicit refusals / quoted text and apostrophes / keyword boundaries / transcription / verification metadata / Japanese compatibility";
        report["englishSearchExamples"]=positive.Length+negative.Length+independent.Length;
    }
}
}
