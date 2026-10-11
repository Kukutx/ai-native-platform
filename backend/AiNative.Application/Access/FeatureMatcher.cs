namespace AiNative.Application.Access;

/// <summary>
/// 功能权限匹配：精确匹配、模块通配（"tasks.*"）或全部（"*"）。
/// </summary>
public static class FeatureMatcher
{
    public const string All = "*";

    public static bool IsGranted(IEnumerable<string> grants, string feature)
    {
        foreach (var grant in grants)
        {
            if (Matches(grant, feature))
            {
                return true;
            }
        }

        return false;
    }

    public static bool Matches(string grant, string feature)
    {
        if (grant == All || string.Equals(grant, feature, StringComparison.Ordinal))
        {
            return true;
        }

        return grant.EndsWith(".*", StringComparison.Ordinal)
               && feature.StartsWith(grant[..^1], StringComparison.Ordinal);
    }
}
