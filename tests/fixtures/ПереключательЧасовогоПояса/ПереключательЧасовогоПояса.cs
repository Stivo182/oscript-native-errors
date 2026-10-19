using System.Reflection;
using OneScript.Contexts;
using OneScript.Execution;
using OneScript.Types;
using ScriptEngine.Machine.Contexts;

namespace NativeErrors.Tests.Fixtures;

[ContextClass("ПереключательЧасовогоПояса")]
public sealed class TimeZoneSwitcher : AutoContext<TimeZoneSwitcher>
{
    private const string WindowsTimeZoneId = "Russian Standard Time";
    private const string UnixTimeZoneId = "Europe/Moscow";

    // shortcut: relies on the private .NET 8 cache layout; replace with a public override when OneScript exposes one.
    private static readonly FieldInfo CachedDataField = typeof(TimeZoneInfo).GetField(
        "s_cachedData",
        BindingFlags.Static | BindingFlags.NonPublic
    ) ?? throw new InvalidOperationException("Не найден кэш часовых зон .NET.");

    private static readonly FieldInfo LocalTimeZoneField = CachedDataField.FieldType.GetField(
        "_localTimeZone",
        BindingFlags.Instance | BindingFlags.NonPublic
    ) ?? throw new InvalidOperationException("Не найдена локальная часовая зона в кэше .NET.");

    private object? _cachedData;
    private object? _initialLocalTimeZone;
    private bool _isTimeZoneChanged;

    [ScriptConstructor]
    public static TimeZoneSwitcher Constructor(TypeActivationContext context) => new();

    [ContextMethod("УстановитьПоложительнуюЗону")]
    public void SetPositiveTimeZone()
    {
        if (_isTimeZoneChanged)
            return;

        _cachedData = CachedDataField.GetValue(null)
            ?? throw new InvalidOperationException("Кэш часовых зон .NET не инициализирован.");
        _initialLocalTimeZone = LocalTimeZoneField.GetValue(_cachedData);
        LocalTimeZoneField.SetValue(_cachedData, TimeZoneInfo.FindSystemTimeZoneById(GetPositiveTimeZoneId()));

        _isTimeZoneChanged = true;
    }

    [ContextMethod("ВосстановитьИсходнуюЗону")]
    public void RestoreInitialTimeZone()
    {
        if (!_isTimeZoneChanged)
            return;

        if (ReferenceEquals(CachedDataField.GetValue(null), _cachedData))
            LocalTimeZoneField.SetValue(_cachedData, _initialLocalTimeZone);

        _isTimeZoneChanged = false;
    }

    private static string GetPositiveTimeZoneId() => OperatingSystem.IsWindows()
        ? WindowsTimeZoneId
        : UnixTimeZoneId;
}
