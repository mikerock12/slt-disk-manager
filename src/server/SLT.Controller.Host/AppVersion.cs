using System.Reflection;

namespace Slt.Controller;

// Release name (VERSION file) stamped at build time. The wire protocol version stays "0.1.0".
public static class AppVersion
{
    public static string Current { get; } = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";
}
