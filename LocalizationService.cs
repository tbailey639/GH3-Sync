using System;
using System.Collections.Generic;

namespace GH3_Sync;

public record LanguageInfo(string Code, string NativeName, string EnglishName, string FlagEmoji);

public static class LocalizationService
{
    public static readonly IReadOnlyList<LanguageInfo> SupportedLanguages = new List<LanguageInfo>
    {
        new("en", "English", "English", "🇺🇸"),
        new("es", "Español", "Spanish", "🇪🇸"),
        new("pt-BR", "Português (Brasil)", "Portuguese (Brazil)", "🇧🇷"),
        new("fr", "Français", "French", "🇫🇷"),
        new("de", "Deutsch", "German", "🇩🇪"),
        new("it", "Italiano", "Italian", "🇮🇹"),
        new("ja", "日本語", "Japanese", "🇯🇵"),
        new("ko", "한국어", "Korean", "🇰🇷")
    };

    public static string CurrentLanguage { get; private set; } = "en";

    public static event Action? OnLanguageChanged;

    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
    {
        ["en"] = new()
        {
            ["nav_customs"] = "🎸 My Customs",
            ["nav_creator"] = "⚡ Song Creator",
            ["nav_tools"] = "🛠 Tools & Stems",
            ["nav_friends"] = "👥 Friends & Trust",
            ["nav_p2p"] = "🌐 P2P Sync & Lobby",
            ["nav_import"] = "📦 Import & Convert",
            ["nav_career"] = "💾 Career Saves",
            ["nav_settings"] = "⚙ Settings",
            ["btn_play_gh3"] = "Play in GH3",
            ["btn_compile_song"] = "Create & Compile Song",
            ["btn_sync_friend"] = "Sync With Friend",
            ["btn_rock_on"] = "🎸 ROCK ON!",
            ["btn_cancel"] = "✕ CANCEL",
            ["stage_ready"] = "STAGE HUD READY",
            ["stage_compiling"] = "COMPILING SONG STEMS",
            ["stage_downloading"] = "TRANSFERRING P2P AUDIO",
            ["stage_complete"] = "ENCORE COMPLETE",
            ["opt_audio_studio"] = "Audio Studio & Loudness",
            ["opt_controller_studio"] = "Guitar & Controller Studio",
            ["opt_performance"] = "Engine & Windows 11 Performance",
            ["opt_security"] = "Zero-Trust & Quarantine Shield"
        },
        ["es"] = new()
        {
            ["nav_customs"] = "🎸 Mis Canciones",
            ["nav_creator"] = "⚡ Creador de Canciones",
            ["nav_tools"] = "🛠 Herramientas y Stems",
            ["nav_friends"] = "👥 Amigos y Confianza",
            ["nav_p2p"] = "🌐 Sincronización P2P",
            ["nav_import"] = "📦 Importar y Convertir",
            ["nav_career"] = "💾 Partidas de Carrera",
            ["nav_settings"] = "⚙ Ajustes",
            ["btn_play_gh3"] = "Jugar en GH3",
            ["btn_compile_song"] = "Crear y Compilar Canción",
            ["btn_sync_friend"] = "Sincronizar con Amigo",
            ["btn_rock_on"] = "🎸 ¡A ROCKEAR!",
            ["btn_cancel"] = "✕ CANCELAR",
            ["stage_ready"] = "HUD DE ESCENARIO LISTO",
            ["stage_compiling"] = "COMPILANDO STEMS DE AUDIO",
            ["stage_downloading"] = "TRANSFIRIENDO AUDIO P2P",
            ["stage_complete"] = "¡ENCORE COMPLETO!",
            ["opt_audio_studio"] = "Estudio de Audio y Volumen",
            ["opt_controller_studio"] = "Estudio de Guitarra y Mando",
            ["opt_performance"] = "Rendimiento y Windows 11",
            ["opt_security"] = "Escudo Zero-Trust y Cuarentena"
        },
        ["pt-BR"] = new()
        {
            ["nav_customs"] = "🎸 Minhas Músicas",
            ["nav_creator"] = "⚡ Criador de Músicas",
            ["nav_tools"] = "🛠 Ferramentas e Stems",
            ["nav_friends"] = "👥 Amigos e Confiança",
            ["nav_p2p"] = "🌐 Sincronização P2P",
            ["nav_import"] = "📦 Importar e Converter",
            ["nav_career"] = "💾 Salvar Carreira",
            ["nav_settings"] = "⚙ Configurações",
            ["btn_play_gh3"] = "Jogar no GH3",
            ["btn_compile_song"] = "Criar e Compilar Música",
            ["btn_sync_friend"] = "Sincronizar com Amigo",
            ["btn_rock_on"] = "🎸 VAMOS DETONAR!",
            ["btn_cancel"] = "✕ CANCELAR",
            ["stage_ready"] = "HUD DE PALCO PRONTO",
            ["stage_compiling"] = "COMPILANDO STEMS DE ÁUDIO",
            ["stage_downloading"] = "TRANSFERINDO ÁUDIO P2P",
            ["stage_complete"] = "ENCORE COMPLETO!",
            ["opt_audio_studio"] = "Estúdio de Áudio e Loudness",
            ["opt_controller_studio"] = "Estúdio de Guitarra e Controle",
            ["opt_performance"] = "Desempenho e Windows 11",
            ["opt_security"] = "Escudo Zero-Trust e Quarentena"
        },
        ["fr"] = new()
        {
            ["nav_customs"] = "🎸 Mes Morceaux",
            ["nav_creator"] = "⚡ Créateur de Chansons",
            ["nav_tools"] = "🛠 Outils & Stems",
            ["nav_friends"] = "👥 Amis & Confiance",
            ["nav_p2p"] = "🌐 Synchro P2P & Salon",
            ["nav_import"] = "📦 Importer & Convertir",
            ["nav_career"] = "💾 Sauvegardes Carrière",
            ["nav_settings"] = "⚙ Paramètres",
            ["btn_play_gh3"] = "Jouer dans GH3",
            ["btn_compile_song"] = "Créer & Compiler la Chanson",
            ["btn_sync_friend"] = "Synchroniser avec un Ami",
            ["btn_rock_on"] = "🎸 EN AVANT LE ROCK !",
            ["btn_cancel"] = "✕ ANNULER",
            ["stage_ready"] = "HUD SCÈNE PRÊT",
            ["stage_compiling"] = "COMPILATION DES PISTES AUDIO",
            ["stage_downloading"] = "TRANSFERT DU FICHIER AUDIO P2P",
            ["stage_complete"] = "RAPPEL TERMINÉ !",
            ["opt_audio_studio"] = "Studio Audio & Volume",
            ["opt_controller_studio"] = "Studio Guitare & Manette",
            ["opt_performance"] = "Performances & Windows 11",
            ["opt_security"] = "Bouclier Zéro-Trust & Quarantaine"
        },
        ["de"] = new()
        {
            ["nav_customs"] = "🎸 Meine Songs",
            ["nav_creator"] = "⚡ Song-Ersteller",
            ["nav_tools"] = "🛠 Werkzeuge & Stems",
            ["nav_friends"] = "👥 Freunde & Vertrauen",
            ["nav_p2p"] = "🌐 P2P-Sync & Lobby",
            ["nav_import"] = "📦 Import & Konvertierung",
            ["nav_career"] = "💾 Karriere-Speicherstände",
            ["nav_settings"] = "⚙ Einstellungen",
            ["btn_play_gh3"] = "In GH3 spielen",
            ["btn_compile_song"] = "Song erstellen & kompilieren",
            ["btn_sync_friend"] = "Mit Freund synchronisieren",
            ["btn_rock_on"] = "🎸 ROCK ON!",
            ["btn_cancel"] = "✕ ABBRECHEN",
            ["stage_ready"] = "BÜHNEN-HUD BEREIT",
            ["stage_compiling"] = "AUDIO-STEMS WERDEN KOMPILIERT",
            ["stage_downloading"] = "P2P-AUDIO WIRD ÜBERTRAGEN",
            ["stage_complete"] = "ZUGABE ABGESCHLOSSEN!",
            ["opt_audio_studio"] = "Audio-Studio & Lautstärke",
            ["opt_controller_studio"] = "Gitarren- & Controller-Studio",
            ["opt_performance"] = "Leistung & Windows 11",
            ["opt_security"] = "Zero-Trust & Quarantäne-Schild"
        },
        ["it"] = new()
        {
            ["nav_customs"] = "🎸 Le Mie Canzoni",
            ["nav_creator"] = "⚡ Creatore Brani",
            ["nav_tools"] = "🛠 Strumenti & Stem",
            ["nav_friends"] = "👥 Amici & Fiducia",
            ["nav_p2p"] = "🌐 Sincronizzazione P2P",
            ["nav_import"] = "📦 Importa & Converti",
            ["nav_career"] = "💾 Salvataggi Carriera",
            ["nav_settings"] = "⚙ Impostazioni",
            ["btn_play_gh3"] = "Gioca in GH3",
            ["btn_compile_song"] = "Crea & Compila Brano",
            ["btn_sync_friend"] = "Sincronizza con Amico",
            ["btn_rock_on"] = "🎸 ROCK ON!",
            ["btn_cancel"] = "✕ ANNULLA",
            ["stage_ready"] = "HUD DEL PALCO PRONTO",
            ["stage_compiling"] = "COMPILAZIONE DELLE TRACCE AUDIO",
            ["stage_downloading"] = "TRASFERIMENTO AUDIO P2P",
            ["stage_complete"] = "BIS COMPLETATO!",
            ["opt_audio_studio"] = "Studio Audio & Volume",
            ["opt_controller_studio"] = "Studio Chitarra & Controller",
            ["opt_performance"] = "Prestazioni & Windows 11",
            ["opt_security"] = "Scudo Zero-Trust & Quarantena"
        },
        ["ja"] = new()
        {
            ["nav_customs"] = "🎸 カスタム楽曲",
            ["nav_creator"] = "⚡ ソングクリエイター",
            ["nav_tools"] = "🛠 ツール＆音源分離",
            ["nav_friends"] = "👥 フレンド＆信頼リスト",
            ["nav_p2p"] = "🌐 P2P同期＆ロビー",
            ["nav_import"] = "📦 インポート＆変換",
            ["nav_career"] = "💾 キャリアセーブ",
            ["nav_settings"] = "⚙ 設定",
            ["btn_play_gh3"] = "GH3で今すぐプレイ",
            ["btn_compile_song"] = "楽曲作成＆コンパイル",
            ["btn_sync_friend"] = "フレンドと同期",
            ["btn_rock_on"] = "🎸 ロックオン！",
            ["btn_cancel"] = "✕ キャンセル",
            ["stage_ready"] = "ステージHUD 準備完了",
            ["stage_compiling"] = "オーディオステムをコンパイル中",
            ["stage_downloading"] = "P2Pオーディオ転送中",
            ["stage_complete"] = "アンコール完了！",
            ["opt_audio_studio"] = "オーディオスタジオ＆ラウドネス",
            ["opt_controller_studio"] = "ギター＆コントローラースタジオ",
            ["opt_performance"] = "動作最適化＆Windows 11",
            ["opt_security"] = "ゼロトラスト＆隔離シールド"
        },
        ["ko"] = new()
        {
            ["nav_customs"] = "🎸 내 커스텀 곡",
            ["nav_creator"] = "⚡ 곡 제작기",
            ["nav_tools"] = "🛠 도구 및 음원 분리",
            ["nav_friends"] = "👥 친구 및 신뢰 관리",
            ["nav_p2p"] = "🌐 P2P 동기화 및 로비",
            ["nav_import"] = "📦 가져오기 및 변환",
            ["nav_career"] = "💾 커리어 세이브",
            ["nav_settings"] = "⚙ 설정",
            ["btn_play_gh3"] = "GH3에서 바로 플레이",
            ["btn_compile_song"] = "곡 생성 및 컴파일",
            ["btn_sync_friend"] = "친구와 동기화",
            ["btn_rock_on"] = "🎸 락온!",
            ["btn_cancel"] = "✕ 취소",
            ["stage_ready"] = "스테이지 HUD 준비 완료",
            ["stage_compiling"] = "오디오 스템 컴파일 중",
            ["stage_downloading"] = "P2P 오디오 전송 중",
            ["stage_complete"] = "앙코르 완료!",
            ["opt_audio_studio"] = "오디오 스튜디오 및 음량 정규화",
            ["opt_controller_studio"] = "기타 및 컨트롤러 스튜디오",
            ["opt_performance"] = "엔진 및 Windows 11 최적화",
            ["opt_security"] = "제로 트러스트 및 격리 실드"
        }
    };

    public static void SetLanguage(string langCode)
    {
        if (string.IsNullOrWhiteSpace(langCode)) return;
        var code = langCode.Trim();
        if (!Strings.ContainsKey(code))
        {
            // Try prefix match (e.g. "pt" -> "pt-BR")
            if (code.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) code = "pt-BR";
            else if (code.StartsWith("es", StringComparison.OrdinalIgnoreCase)) code = "es";
            else if (code.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) code = "fr";
            else if (code.StartsWith("de", StringComparison.OrdinalIgnoreCase)) code = "de";
            else if (code.StartsWith("it", StringComparison.OrdinalIgnoreCase)) code = "it";
            else if (code.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) code = "ja";
            else if (code.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) code = "ko";
            else code = "en";
        }

        CurrentLanguage = code;
        try
        {
            AppConfig.Current.Language = code;
            AppConfig.Save();
        }
        catch { }

        OnLanguageChanged?.Invoke();
    }

    public static string Get(string key, string fallback = "")
    {
        if (Strings.TryGetValue(CurrentLanguage, out var dict) && dict.TryGetValue(key, out var val))
            return val;

        if (Strings.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var enVal))
            return enVal;

        return string.IsNullOrEmpty(fallback) ? key : fallback;
    }
}
