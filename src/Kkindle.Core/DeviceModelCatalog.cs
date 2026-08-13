namespace Kkindle.Core;

/// <summary>
/// 常见的电子书阅读器厂家与型号目录，用于设备型号选择菜单。
/// </summary>
public static class DeviceModelCatalog
{
    public sealed record Vendor(string Name, IReadOnlyList<string> Models);

    public static IReadOnlyList<Vendor> Vendors { get; } =
    [
        new("Kindle",
        [
            "Kindle（基础版）",
            "Kindle Paperwhite",
            "Kindle Paperwhite 5",
            "Kindle Paperwhite 11 代",
            "Kindle Paperwhite 12 代",
            "Kindle Oasis",
            "Kindle Voyage",
            "Kindle Scribe 1 代",
            "Kindle Scribe 2 代",
            "Kindle Scribe 3 代"
        ]),
        new("汉王",
        [
            "汉王 N10",
            "汉王 N10 Touch",
            "汉王 N10 mini",
            "汉王 Clear",
            "汉王 E10",
            "汉王 A5"
        ]),
        new("掌阅",
        [
            "掌阅 iReader Ocean 2",
            "掌阅 iReader Ocean 3",
            "掌阅 iReader Ocean 4",
            "掌阅 iReader Smart 3",
            "掌阅 iReader Smart 4",
            "掌阅 iReader Smart X",
            "掌阅 iReader Light 2",
            "掌阅 iReader Light 3",
            "掌阅 iReader Neo"
        ]),
        new("Kobo",
        [
            "Kobo Clara HD",
            "Kobo Clara 2E",
            "Kobo Clara BW",
            "Kobo Libra 2",
            "Kobo Libra Colour",
            "Kobo Sage",
            "Kobo Elipsa 2E",
            "Kobo Forma",
            "Kobo Nia"
        ])
    ];
}
