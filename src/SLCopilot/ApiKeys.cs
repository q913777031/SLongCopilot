using Microsoft.VisualStudio.Shell;
using SLCopilot.Core;

namespace SLCopilot;

/// <summary>
/// 读取 API 密钥：先取 Windows 凭据管理器中该配置的密钥，再按环境变量名读取（进程、用户级、系统级）。
/// 找不到时返回 null，并通过 <paramref name="problem"/> 给出面向用户的中文原因。可在任意线程调用。
/// </summary>
public static class ApiKeys
{
    public static string? Read(ProviderSettings settings, string profileId, out string problem)
    {
        problem = "";
        try
        {
            if (CredentialStore.Read(CredentialStore.TargetFor(profileId)) is { } stored) return stored;
        }
        catch (Exception ex) { ActivityLog.LogWarning("SLCopilot", $"Reading the stored key failed: {ex.Message}"); }
        var name = settings.KeyEnvironmentVariable.Trim();
        if (name.Length == 0)
        {
            problem = "尚未配置 API 密钥：请在“设置”中粘贴密钥并点击“保存密钥”。";
            return null;
        }
        if (name.StartsWith("sk-", StringComparison.Ordinal) || name.Length > 64 || name.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
        {
            problem = "“环境变量名”一栏应填写变量名（如 OPENAI_API_KEY）；密钥本身请粘贴到“API 密钥”框并保存。";
            return null;
        }
        var key = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(key)) key = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(key)) key = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        if (string.IsNullOrWhiteSpace(key))
        {
            problem = $"未保存密钥，也找不到环境变量 {name}。请在“设置”中粘贴密钥并点击“保存密钥”。";
            return null;
        }
        return key;
    }
}
