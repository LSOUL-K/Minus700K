using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace Minus700K {
internal static class SpeechModel {
    internal const string RelativePath="vendor/ggml-small-q5_1.bin";
    internal const long Bytes=190085487;
    internal const string Sha256="ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb";
    internal const string Url="https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin";
    internal static bool IsDefault(string root,string configured) {
        return string.Equals(SettingsStorage.Resolve(root,configured),Path.GetFullPath(Path.Combine(root,RelativePath)),StringComparison.OrdinalIgnoreCase);
    }
    internal static VerifiedModelFile FileFor(string root) { return new VerifiedModelFile(Path.Combine(root,RelativePath),new Uri(Url),Bytes,Sha256); }
    internal static bool NeedsDownload(string root,Settings settings) { return IsDefault(root,settings.WhisperModel) && !FileFor(root).IsPresent; }
}

// Only verified downloads are published at the final path. The same path is
// retained across updates, including upgrades from the former bundled model.
internal sealed class VerifiedModelFile {
    readonly string target,sha256; readonly Uri source; readonly long bytes;
    internal VerifiedModelFile(string target,Uri source,long bytes,string sha256) {
        if(source.Scheme!="https" && !(source.IsLoopback && source.Scheme=="http")) throw new ArgumentException("Model downloads require HTTPS.");
        this.target=Path.GetFullPath(target); this.source=source; this.bytes=bytes; this.sha256=sha256;
    }
    internal bool IsPresent { get { var file=new FileInfo(target); return file.Exists && file.Length==bytes; } }
    internal void Ensure(Action<long,long> progress,CancellationToken token) {
        token.ThrowIfCancellationRequested();
        // Previously bundled files were verified at build time. Downloads are
        // verified below; no network request is needed on subsequent launches.
        if(IsPresent) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        string partial=target+".download";
        // A stable, exclusively opened staging file also prevents two processes
        // from downloading to this installation concurrently. Retry starts fresh.
        using(var output=new FileStream(partial,FileMode.OpenOrCreate,FileAccess.Write,FileShare.None)) {
            try {
                output.SetLength(0);
                ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
                var request=(HttpWebRequest)WebRequest.Create(source);
                request.Timeout=30000; request.ReadWriteTimeout=30000;
                request.UserAgent="Minus700K/0.1";
                using(token.Register(request.Abort))
                using(var response=(HttpWebResponse)request.GetResponse()) {
                    if(response.ResponseUri.Scheme!="https" && !response.ResponseUri.IsLoopback) throw new InvalidDataException("Insecure model redirect.");
                    if(response.StatusCode!=HttpStatusCode.OK || (response.ContentLength>=0 && response.ContentLength!=bytes)) throw new InvalidDataException("Unexpected model response length.");
                    using(var input=response.GetResponseStream()) using(var hash=SHA256.Create()) {
                        byte[] buffer=new byte[65536]; long received=0; int count; var clock=Stopwatch.StartNew();
                        if(progress!=null) progress(0,bytes);
                        while((count=input.Read(buffer,0,buffer.Length))>0) {
                            token.ThrowIfCancellationRequested(); received+=count;
                            if(received>bytes) throw new InvalidDataException("Model exceeds expected length.");
                            output.Write(buffer,0,count); hash.TransformBlock(buffer,0,count,buffer,0);
                            if(clock.ElapsedMilliseconds>=100) { if(progress!=null) progress(received,bytes); clock.Restart(); }
                        }
                        token.ThrowIfCancellationRequested(); hash.TransformFinalBlock(new byte[0],0,0);
                        string actual=BitConverter.ToString(hash.Hash).Replace("-","").ToLowerInvariant();
                        if(received!=bytes || !string.Equals(actual,sha256,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Model verification failed.");
                        output.Flush(true);
                        if(progress!=null) progress(received,bytes);
                    }
                }
            } catch { token.ThrowIfCancellationRequested(); throw; }
        }
        token.ThrowIfCancellationRequested();
        // Replacement is atomic; interruption never makes a partial model look
        // usable. A failed staging file is overwritten on the next retry.
        if(File.Exists(target)) File.Replace(partial,target,null); else File.Move(partial,target);
    }
}
}
