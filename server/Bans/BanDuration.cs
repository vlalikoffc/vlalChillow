namespace StandChillow.LanServer.Bans;

/// <summary>
/// Срок бана: разбор того, что админ написал в команде, и показ остатка игроку.
/// </summary>
/// <remarks>
/// Границы — из ТЗ: минимум 10 минут, максимум 14 дней. Верхняя граница важна не
/// из вредности: IP на LAN выдаёт DHCP, и вечный бан адреса рано или поздно ударит по
/// другому телефону, которому этот адрес достанется.
/// </remarks>
public static class BanDuration
{
    public const int DefaultMinMinutes = 10;
    public const int DefaultMaxDays = 14;

    /// <summary>Срок, если админ его не написал (не используется: срок обязателен).</summary>
    public static TimeSpan Default => TimeSpan.FromMinutes(30);

    public static TimeSpan DefaultMin => TimeSpan.FromMinutes(DefaultMinMinutes);

    public static TimeSpan DefaultMax => TimeSpan.FromDays(DefaultMaxDays);

    /// <summary>Разбор с дефолтными границами 10м…14д.</summary>
    public static bool TryParse(string? text, out TimeSpan duration, out string error) =>
        TryParse(text, DefaultMin, DefaultMax, out duration, out error);

    /// <summary>
    /// Разбор формы <c>30m</c>, <c>6h</c>, <c>3d</c>, <c>1d12h</c>, <c>30м</c>, <c>6ч</c>, <c>3д</c>.
    /// Без единицы число считается минутами (<c>30</c> = 30 минут).
    /// </summary>
    public static bool TryParse(
        string? text,
        TimeSpan min,
        TimeSpan max,
        out TimeSpan duration,
        out string error)
    {
        duration = TimeSpan.Zero;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "не указан срок бана — например 30m, 6h, 3d";
            return false;
        }

        var s = text.Trim().ToLowerInvariant()
            .Replace(" ", string.Empty)
            .Replace("_", string.Empty);

        if (s is "perm" or "permanent" or "навсегда" or "вечно" or "inf" or "бесконечно")
        {
            error = $"бессрочного бана нет: максимум {FormatShort(max)}".TrimEnd();
            return false;
        }

        long seconds = 0;
        var i = 0;
        while (i < s.Length)
        {
            var digitStart = i;
            while (i < s.Length && char.IsDigit(s[i]))
                i++;
            if (digitStart == i)
            {
                error = $"'{text}' — это не срок. Примеры: 30m, 6h, 3d";
                return false;
            }

            if (!long.TryParse(s.AsSpan(digitStart, i - digitStart), out var value) || value < 0)
            {
                error = $"'{text}' — число срока не читается";
                return false;
            }

            var unitStart = i;
            while (i < s.Length && !char.IsDigit(s[i]))
                i++;
            var unit = s[unitStart..i];

            long multiplier;
            switch (unit)
            {
                case "":
                case "м" or "m" or "мин" or "минута" or "минуты" or "минут":
                    multiplier = 60;
                    break;
                case "ч" or "h" or "час" or "часа" or "часов" or "hr" or "hrs":
                    multiplier = 3600;
                    break;
                case "д" or "d" or "дн" or "день" or "дня" or "дней" or "сут" or "сутки":
                    multiplier = 86_400;
                    break;
                case "н" or "w" or "нед" or "неделя" or "недели" or "недель":
                    multiplier = 604_800;
                    break;
                case "с" or "s" or "сек" or "секунда" or "секунды" or "секунд":
                    multiplier = 1;
                    break;
                default:
                    error = $"непонятная единица '{unit}': m — минуты, h — часы, d — дни";
                    return false;
            }

            if (value > 1_000_000)
            {
                error = "слишком большой срок";
                return false;
            }

            seconds += value * multiplier;

            // Дальше потолка считать незачем: всё равно откажем, а переполнение не нужно.
            if (seconds > 400L * 86_400)
                break;
        }

        duration = TimeSpan.FromSeconds(seconds);

        if (duration < min)
        {
            error = $"минимум {FormatShort(min)} (ты указал {FormatShort(duration)})";
            return false;
        }

        if (duration > max)
        {
            error = $"максимум {FormatShort(max)} (ты указал {FormatShort(duration)})";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Остаток в виде «1д 2ч 3м 04с» — все четыре единицы, как просили.
    /// Секунды округляются вверх: пока бан активен, «0с» показывать нельзя.
    /// </summary>
    public static string FormatRemaining(TimeSpan remaining)
    {
        var total = (long)Math.Ceiling(remaining.TotalSeconds);
        if (total < 0)
            total = 0;

        var days = total / 86_400;
        var hours = total % 86_400 / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        return $"{days}д {hours}ч {minutes}м {seconds:00}с";
    }

    /// <summary>Короткая форма для сообщений: «30м», «6ч», «1ч 30м», «3д».</summary>
    public static string FormatShort(TimeSpan duration)
    {
        var totalMinutes = (long)Math.Round(duration.TotalMinutes);
        if (totalMinutes < 60)
            return $"{totalMinutes}м";

        var hours = totalMinutes / 60;
        var restMinutes = totalMinutes % 60;
        if (hours < 24)
            return restMinutes == 0 ? $"{hours}ч" : $"{hours}ч {restMinutes}м";

        var days = hours / 24;
        var restHours = hours % 24;
        return restHours == 0 ? $"{days}д" : $"{days}д {restHours}ч";
    }
}
