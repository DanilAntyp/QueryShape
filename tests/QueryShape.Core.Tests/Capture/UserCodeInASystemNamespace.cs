namespace System.Fake;

/// <summary>User code in a System.* namespace must still count as a call site (frames are skipped by assembly, not namespace).</summary>
public static class UserCodeInASystemNamespace
{
}
