// UI language. Every user-visible string goes through L.T("English text") or
// L.F("English {0} format", args); the English text is the key into the Bulgarian table.
// tests\CoreTests.cs scans the sources and fails if any key has no translation.
using System.Collections.Generic;
using System.Globalization;

namespace OrclCM
{
    enum LanguageMode { System, English, Bulgarian }

    static class L
    {
        public static bool Bulgarian;

        public static void Set(LanguageMode mode) =>
            Bulgarian = mode == LanguageMode.Bulgarian ||
                        (mode == LanguageMode.System && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "bg");

        public static string T(string english) =>
            Bulgarian && Bg.TryGetValue(english, out string bg) ? bg : english;

        public static string F(string englishFormat, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, T(englishFormat), args);

        public static readonly Dictionary<string, string> Bg = new Dictionary<string, string>
        {
            // battery state
            ["Charging"] = "Зарежда се",
            ["Plugged in · discharging"] = "Включено · разрежда се",
            ["On battery"] = "На батерия",
            ["Discharging"] = "Разрежда се",
            ["Plugged in · not charging"] = "Включено · не се зарежда",
            ["Idle"] = "В покой",
            ["Plugged in"] = "Включено",
            ["Status unknown"] = "Неизвестно състояние",
            [" · rate unavailable"] = " · мощността не е налична",
            ["Reading battery…"] = "Четене на батерията…",
            ["No battery data"] = "Няма данни за батерията",
            ["No battery found"] = "Не е открита батерия",
            ["Battery {0}%"] = "Батерия {0}%",
            ["Stale · last reading {0} ago"] = "Остаряло · последно отчитане преди {0}",
            ["Waiting for a new reading"] = "Изчакване на ново отчитане",
            ["{0} batteries"] = "{0} батерии",
            ["Rate reported in relative units"] = "Мощността се отчита в относителни единици",
            ["read failed"] = "грешка при четене",
            ["{0} s"] = "{0} с",

            // durations, estimates, health, session, power draw
            ["<1 min"] = "<1 мин",
            ["{0} min"] = "{0} мин",
            ["{0} h {1} min"] = "{0} ч {1} мин",
            ["Full in {0}"] = "Пълна след {0}",
            ["Empty in {0}"] = "Изтощена след {0}",
            ["Health {0}%  ({1} of {2} Wh)"] = "Състояние {0}%  ({1} от {2} Wh)",
            ["{0} cycles"] = "{0} цикъла",
            ["Plugged in for {0} · {1} Wh · avg {2} W · peak {3} W"] = "Включено от {0} · {1} Wh · средно {2} W · пик {3} W",
            ["On battery for {0} · {1} Wh · avg {2} W · peak {3} W"] = "На батерия от {0} · {1} Wh · средно {2} W · пик {3} W",
            ["Since OrclCM started {0} · {1} Wh · avg {2} W · peak {3} W"] = "От стартирането на OrclCM {0} · {1} Wh · средно {2} W · пик {3} W",
            ["Laptop using {0} W"] = "Лаптопът консумира {0} W",
            ["Laptop using {0} W — higher than usual"] = "Лаптопът консумира {0} W – повече от обичайното",

            // alerts and sleep
            ["Battery at {0}%"] = "Батерия {0}%",
            ["Charged to your {0}% limit - you can unplug the charger."] = "Заредена до зададените {0}% – можете да изключите зарядното.",
            ["Battery is low - plug in the charger."] = "Батерията е изтощена – включете зарядното.",
            ["Plugged in but draining"] = "Включено, но се разрежда",
            ["The battery is losing {0} W although the charger is connected - it may be too weak, or charging may be paused."] =
                "Батерията губи {0} W, въпреки че зарядното е включено – може да е твърде слабо или зареждането е спряно.",
            ["Battery use during sleep"] = "Разход на батерията по време на сън",
            ["{0} asleep: {1}% ({2} Wh, avg {3} W)"] = "{0} в сън: {1}% ({2} Wh, средно {3} W)",
            ["{0} asleep: {1}%"] = "{0} в сън: {1}%",

            // menus
            ["Show / hide window"] = "Покажи / скрий прозореца",
            ["Always on top"] = "Винаги отгоре",
            ["Start with Windows"] = "Стартиране с Windows",
            ["Mini widget"] = "Мини прозорче",
            ["Theme"] = "Тема",
            ["System"] = "Системна",
            ["Dark"] = "Тъмна",
            ["Light"] = "Светла",
            ["Language"] = "Език",
            ["Graph range"] = "Обхват на графиката",
            ["4 min"] = "4 мин",
            ["1 h"] = "1 ч",
            ["24 h"] = "24 ч",
            ["Battery log…"] = "Дневник на батерията…",
            ["Settings…"] = "Настройки…",
            ["Export history…"] = "Експорт на историята…",
            ["Help"] = "Помощ",
            ["About OrclCM"] = "За OrclCM",
            ["Exit"] = "Изход",
            ["Exit OrclCM"] = "Изход от OrclCM",
            ["System language"] = "Езикът на системата",

            // dialogs
            ["OrclCM settings"] = "Настройки на OrclCM",
            ["Start with Windows (in the tray)"] = "Стартиране с Windows (в областта за уведомяване)",
            ["Notifications"] = "Известия",
            ["Alert when charged to"] = "Известие при заряд до",
            ["Alert when battery drops to"] = "Известие при спад до",
            ["Alert when plugged in but the battery is draining"] = "Известие, ако е включено, но батерията се разрежда",
            ["Report battery use during sleep"] = "Отчет за разхода на батерията по време на сън",
            ["OK"] = "ОК",
            ["Cancel"] = "Отказ",
            ["Close"] = "Затвори",
            ["Version {0}"] = "Версия {0}",
            ["Live laptop battery charging / draining wattage in your tray."] =
                "Мощност на зареждане / разреждане на батерията в реално време в областта за уведомяване.",
            ["Copyright © 2026 Oracooll. MIT license.\nBased on the MIT-licensed Charge Meter."] =
                "Авторско право © 2026 Oracooll. Лиценз MIT.\nБазирано на Charge Meter (лиценз MIT).",
            ["OrclCM help"] = "Помощ за OrclCM",
            ["Export battery history"] = "Експорт на историята на батерията",
            ["CSV file (*.csv)|*.csv"] = "CSV файл (*.csv)|*.csv",
            ["No readings recorded yet."] = "Все още няма отчитания.",
            ["Could not save the file:\n{0}"] = "Файлът не може да бъде записан:\n{0}",
            ["Could not change Start with Windows:\n{0}"] = "Стартирането с Windows не може да бъде променено:\n{0}",

            // updates
            ["Check for updates…"] = "Проверка за актуализации…",
            ["Check for updates"] = "Проверка за актуализации",
            ["Check for updates automatically"] = "Автоматична проверка за актуализации",
            ["OrclCM {0} is available"] = "Налична е OrclCM {0}",
            ["Click to update (you have {0})."] = "Щракнете, за да актуализирате (имате {0}).",
            ["OrclCM {0} is available (you have {1}).\n\nDownload and install it now? OrclCM will restart."] =
                "Налична е OrclCM {0} (имате {1}).\n\nДа се изтегли и инсталира ли сега? OrclCM ще се рестартира.",
            ["You have the latest version ({0})."] = "Имате най-новата версия ({0}).",
            ["Could not check for updates:\n{0}"] = "Проверката за актуализации е неуспешна:\n{0}",
            ["Could not install the update:\n{0}\n\nThe release page will open so you can download it yourself."] =
                "Актуализацията не може да бъде инсталирана:\n{0}\n\nЩе се отвори страницата на версията, за да я изтеглите ръчно.",
            ["The download is not a valid program."] = "Изтегленият файл не е валидна програма.",
            ["The download does not match the published checksum."] = "Изтегленият файл не съвпада с публикуваната контролна сума.",
            ["Downloading update…"] = "Изтегляне на актуализацията…",
            ["The new version did not start."] = "Новата версия не стартира.",

            // battery log window
            ["Battery log"] = "Дневник на батерията",
            ["Sessions"] = "Сесии",
            ["Health"] = "Състояние",
            ["Start"] = "Начало",
            ["Duration"] = "Продължителност",
            ["Type"] = "Вид",
            ["Battery"] = "Батерия",
            ["Energy"] = "Енергия",
            ["Average"] = "Средно",
            ["Peak"] = "Пик",
            ["Sleep"] = "Сън",
            ["Date"] = "Дата",
            ["Full charge"] = "Пълен заряд",
            ["When new"] = "Като нова",
            ["Cycles"] = "Цикли",
            ["Open log folder"] = "Отвори папката с дневника",
            ["No sessions recorded yet."] = "Все още няма записани сесии.",
            ["Battery health over time"] = "Състояние на батерията във времето",
            ["Health is recorded once a day while OrclCM runs."] = "Състоянието се записва веднъж дневно, докато OrclCM работи.",
        };
    }
}
