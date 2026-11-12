using System;

namespace Minus700K {
internal static class CoreAudioDevices {
    static readonly Type EnumeratorType=Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
    internal static object CreateEnumerator() {
        // Windows can reuse the enumerator's COM identity across callers. The
        // CLR then reuses its RCW, so separate strongly typed ComImport classes
        // for this CLSID would fail their generated concrete-class cast. Keep
        // activation untyped and let each caller query its required interface.
        return Activator.CreateInstance(EnumeratorType);
    }
}
}
