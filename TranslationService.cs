namespace KeyboardCounter;

/// <summary>
/// 根据配置创建具体的翻译接口实现
/// </summary>
public static class TranslationService
{
    /// <summary>当前配置是否可用；不可用时返回 null</summary>
    public static ITranslationProvider? Create(TranslateSettings settings)
    {
        if (settings == null || !settings.IsConfigured)
            return null;

        return settings.Provider == TranslateProvider.Baidu
            ? new BaiduTranslator(settings.BaiduAppId, settings.BaiduAppSecret)
            : new YoudaoTranslator(settings.YoudaoAppKey, settings.YoudaoAppSecret);
    }
}
