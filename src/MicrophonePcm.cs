using System;

namespace Minus700K {
// Convert native exclusive-mode formats after releasing the microphone. The
// Blackman-windowed sinc suppresses frequencies above the new Nyquist limit;
// linear interpolation alone would fold them into speech-band noise.
internal static class MicrophonePcm {
    internal const int MaximumInputBytes=256*1024*1024;
    const int OutputRate=16000,Phases=256;
    internal static byte[] ToMono16k(byte[] source,int sampleRate,int channels,int bitsPerSample,int validBits,bool floatingPoint) {
        if(source==null) throw new ArgumentNullException("source");
        if(sampleRate<8000 || sampleRate>384000) throw new ArgumentOutOfRangeException("sampleRate");
        if(channels<1 || channels>32) throw new ArgumentOutOfRangeException("channels");
        if(floatingPoint ? (bitsPerSample!=32 && bitsPerSample!=64) || validBits!=bitsPerSample : (bitsPerSample!=8 && bitsPerSample!=16 && bitsPerSample!=24 && bitsPerSample!=32) || validBits<1 || validBits>bitsPerSample)
            throw new ArgumentException("Unsupported microphone sample format.");
        int bytesPerSample=bitsPerSample/8,frameBytes=bytesPerSample*channels;
        if(source.Length>MaximumInputBytes || source.Length%frameBytes!=0) throw new ArgumentException("Microphone data exceeds the size limit or contains an incomplete sample frame.");
        int frames=source.Length/frameBytes;
        if(frames>MaximumInputBytes/sizeof(float)) throw new ArgumentException("Microphone frame count exceeds the conversion memory limit.");
        long outputFrames=((long)frames*OutputRate+sampleRate/2)/sampleRate;
        if(outputFrames>MaximumInputBytes/2) throw new ArgumentException("Converted microphone data exceeds the size limit.");
        byte[] result=new byte[(int)outputFrames*2];
        if(frames==0 || outputFrames==0) return result;
        var mono=new float[frames];
        double scale=Math.Pow(2,validBits-1); int padding=bitsPerSample-validBits;
        for(int frame=0,offset=0;frame<frames;frame++) {
            double sum=0;
            for(int channel=0;channel<channels;channel++,offset+=bytesPerSample) {
                double sample;
                if(floatingPoint) {
                    sample=bitsPerSample==32?BitConverter.ToSingle(source,offset):BitConverter.ToDouble(source,offset);
                    if(double.IsNaN(sample) || double.IsInfinity(sample)) sample=0;
                    // Cap malformed device data before summing to avoid overflow.
                    sample=Math.Max(-1,Math.Min(1,sample));
                } else if(bitsPerSample==8) sample=((source[offset]>>padding)-scale)/scale;
                else {
                    int raw=source[offset]|(source[offset+1]<<8);
                    if(bitsPerSample==16) raw=(short)raw;
                    else {
                        raw|=source[offset+2]<<16;
                        if(bitsPerSample==24) raw=(raw<<8)>>8;
                        else raw|=source[offset+3]<<24;
                    }
                    sample=(raw>>padding)/scale;
                }
                sum+=sample;
            }
            mono[frame]=(float)(sum/channels);
        }
        if(sampleRate==OutputRate) {
            for(int i=0;i<frames;i++) Write(result,i,mono[i]);
            return result;
        }
        double cutoff=0.94*Math.Min(1,(double)OutputRate/sampleRate);
        int radius=(int)Math.Ceiling(24/cutoff),taps=2*radius+1;
        var kernels=new double[Phases+1][];
        for(int phase=0;phase<=Phases;phase++) {
            double fraction=(double)phase/Phases,total=0; var weights=new double[taps];
            for(int tap=0;tap<taps;tap++) {
                double distance=tap-radius-fraction,windowPosition=distance/radius;
                if(Math.Abs(windowPosition)>=1) continue;
                double angle=Math.PI*cutoff*distance;
                double sinc=Math.Abs(angle)<1e-12?1:Math.Sin(angle)/angle;
                double window=0.42+0.5*Math.Cos(Math.PI*windowPosition)+0.08*Math.Cos(2*Math.PI*windowPosition);
                weights[tap]=cutoff*sinc*window; total+=weights[tap];
            }
            for(int tap=0;tap<taps;tap++) weights[tap]/=total;
            kernels[phase]=weights;
        }
        for(int output=0;output<outputFrames;output++) {
            long position=(long)output*sampleRate;
            int center=(int)(position/OutputRate);
            double phasePosition=(double)(position%OutputRate)*Phases/OutputRate;
            int phase=(int)phasePosition; double blend=phasePosition-phase;
            double[] left=kernels[phase],right=kernels[phase+1]; double sum=0;
            for(int tap=0;tap<taps;tap++) {
                int index=Math.Max(0,Math.Min(frames-1,center+tap-radius));
                sum+=mono[index]*(left[tap]+(right[tap]-left[tap])*blend);
            }
            Write(result,output,sum);
        }
        return result;
    }
    static void Write(byte[] target,int frame,double value) {
        int sample=(int)Math.Round(Math.Max(-1,Math.Min(1,value))*32768,MidpointRounding.AwayFromZero);
        sample=Math.Max(short.MinValue,Math.Min(short.MaxValue,sample));
        target[frame*2]=(byte)sample; target[frame*2+1]=(byte)(sample>>8);
    }
}
}
