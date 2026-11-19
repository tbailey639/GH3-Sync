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
            ["btn_soundcheck"] = "🔊 Soundcheck",
            ["btn_autoheal"] = "🩺 Auto-Heal",
            ["btn_poster"] = "🖼️ Tour Poster",
            ["btn_medley"] = "🎸 Medley",
            ["btn_clean_temp"] = "🧹 Clean Cache",
            ["btn_backup_now"] = "💾 Backup Snapshot",
            ["btn_restore_latest"] = "⏮ Restore Latest",
            ["btn_save_settings"] = "💾 Save Settings",
            ["stage_ready"] = "STAGE HUD READY",
            ["stage_compiling"] = "COMPILING SONG STEMS",
            ["stage_downloading"] = "TRANSFERRING P2P AUDIO",
            ["stage_complete"] = "ENCORE COMPLETE",
            ["opt_audio_studio"] = "Audio Studio & Loudness",
            ["opt_controller_studio"] = "Guitar & Controller Studio",
            ["opt_performance"] = "Engine & Windows 11 Performance",
            ["opt_security"] = "Zero-Trust & Quarantine Shield",
            ["opt_tournament"] = "🏆 Rock Tour Tournament",
            ["opt_boss_battle"] = "⚡ Boss Battle Duel"
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
            ["btn_soundcheck"] = "🔊 Prueba de Sonido",
            ["btn_autoheal"] = "🩺 Auto-Reparar",
            ["btn_poster"] = "🖼️ Póster de Gira",
            ["btn_medley"] = "🎸 Popurrí",
            ["btn_clean_temp"] = "🧹 Limpiar Caché",
            ["btn_backup_now"] = "💾 Crear Copia",
            ["btn_restore_latest"] = "⏮ Restaurar Última",
            ["btn_save_settings"] = "💾 Guardar Ajustes",
            ["stage_ready"] = "HUD DE ESCENARIO LISTO",
            ["stage_compiling"] = "COMPILANDO STEMS DE AUDIO",
            ["stage_downloading"] = "TRANSFIRIENDO AUDIO P2P",
            ["stage_complete"] = "¡ENCORE COMPLETO!",
            ["opt_audio_studio"] = "Estudio de Audio y Volumen",
            ["opt_controller_studio"] = "Estudio de Guitarra y Mando",
            ["opt_performance"] = "Rendimiento y Windows 11",
            ["opt_security"] = "Escudo Zero-Trust y Cuarentena",
            ["opt_tournament"] = "🏆 Torneo Gira de Rock",
            ["opt_boss_battle"] = "⚡ Duelo Contra Jefe"
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
            ["btn_soundcheck"] = "🔊 Checagem de Som",
            ["btn_autoheal"] = "🩺 Auto-Curar",
            ["btn_poster"] = "🖼️ Pôster de Turnê",
            ["btn_medley"] = "🎸 Medley",
            ["btn_clean_temp"] = "🧹 Limpar Cache",
            ["btn_backup_now"] = "💾 Salvar Backup",
            ["btn_restore_latest"] = "⏮ Restaurar Recente",
            ["btn_save_settings"] = "💾 Salvar Configurações",
            ["stage_ready"] = "HUD DE PALCO PRONTO",
            ["stage_compiling"] = "COMPILANDO STEMS DE ÁUDIO",
            ["stage_downloading"] = "TRANSFERINDO ÁUDIO P2P",
            ["stage_complete"] = "ENCORE COMPLETO!",
            ["opt_audio_studio"] = "Estúdio de Áudio e Loudness",
            ["opt_controller_studio"] = "Estúdio de Guitarra e Controle",
            ["opt_performance"] = "Motor e Desempenho Windows 11",
            ["opt_security"] = "Escudo Zero-Trust e Quarentena",
            ["opt_tournament"] = "🏆 Torneio Rock Tour",
            ["opt_boss_battle"] = "⚡ Duelo de Chefe"
        },
        ["fr"] = new()
        {
            ["nav_customs"] = "🎸 Mes Morceaux",
            ["nav_creator"] = "⚡ Créateur de Morceaux",
            ["nav_tools"] = "🛠 Outils & Stems",
            ["nav_friends"] = "👥 Amis & Confiance",
            ["nav_p2p"] = "🌐 Sync P2P & Salon",
            ["nav_import"] = "📦 Importer & Convertir",
            ["nav_career"] = "💾 Sauvegardes Carrière",
            ["nav_settings"] = "⚙ Paramètres",
            ["btn_play_gh3"] = "Jouer sur GH3",
            ["btn_compile_song"] = "Créer & Compiler le Morceau",
            ["btn_sync_friend"] = "Synchroniser avec un Ami",
            ["btn_rock_on"] = "🎸 C'EST PARTI !",
            ["btn_cancel"] = "✕ ANNULER",
            ["btn_soundcheck"] = "🔊 Balance Audio",
            ["btn_autoheal"] = "🩺 Auto-Réparation",
            ["btn_poster"] = "🖼️ Affiche de Tournée",
            ["btn_medley"] = "🎸 Medley",
            ["btn_clean_temp"] = "🧹 Nettoyer Cache",
            ["btn_backup_now"] = "💾 Sauvegarder Profil",
            ["btn_restore_latest"] = "⏮ Restaurer Récent",
            ["btn_save_settings"] = "💾 Enregistrer Paramètres",
            ["stage_ready"] = "HUD DE SCÈNE PRÊT",
            ["stage_compiling"] = "COMPILATION DES STEMS AUDIO",
            ["stage_downloading"] = "TRANSFERT AUDIO P2P",
            ["stage_complete"] = "RAPPEL TERMINÉ !",
            ["opt_audio_studio"] = "Studio Audio & Puissance",
            ["opt_controller_studio"] = "Studio Guitare & Manette",
            ["opt_performance"] = "Moteur & Performance Windows 11",
            ["opt_security"] = "Bouclier Zéro-Trust & Quarantaine",
            ["opt_tournament"] = "🏆 Tournoi Rock Tour",
            ["opt_boss_battle"] = "⚡ Duel de Boss"
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
            ["btn_rock_on"] = "🎸 ROCK AN!",
            ["btn_cancel"] = "✕ ABBRECHEN",
            ["btn_soundcheck"] = "🔊 Soundcheck",
            ["btn_autoheal"] = "🩺 Auto-Reparatur",
            ["btn_poster"] = "🖼️ Tourplakat",
            ["btn_medley"] = "🎸 Medley",
            ["btn_clean_temp"] = "🧹 Cache leeren",
            ["btn_backup_now"] = "💾 Backup erstellen",
            ["btn_restore_latest"] = "⏮ Zuletzt wiederherstellen",
            ["btn_save_settings"] = "💾 Einstellungen speichern",
            ["stage_ready"] = "BÜHNEN-HUD BEREIT",
            ["stage_compiling"] = "AUDIO-STEMS KOMPILIEREN",
            ["stage_downloading"] = "P2P-AUDIO ÜBERTRAGEN",
            ["stage_complete"] = "ZUGABE ABGESCHLOSSEN!",
            ["opt_audio_studio"] = "Audiostudio & Lautstärke",
            ["opt_controller_studio"] = "Gitarren- & Controller-Studio",
            ["opt_performance"] = "Engine & Windows 11 Leistung",
            ["opt_security"] = "Zero-Trust & Quarantäne-Schutz",
            ["opt_tournament"] = "🏆 Rock Tour Turnier",
            ["opt_boss_battle"] = "⚡ Boss-Duell"
        },
        ["it"] = new()
        {
            ["nav_customs"] = "🎸 I Miei Brani",
            ["nav_creator"] = "⚡ Crea Brano",
            ["nav_tools"] = "🛠 Strumenti & Stem",
            ["nav_friends"] = "👥 Amici & Fiducia",
            ["nav_p2p"] = "🌐 Sincronizzazione P2P",
            ["nav_import"] = "📦 Importa & Converti",
            ["nav_career"] = "💾 Salvataggi Carriera",
            ["nav_settings"] = "⚙ Impostazioni",
            ["btn_play_gh3"] = "Gioca su GH3",
            ["btn_compile_song"] = "Crea e Compila Brano",
            ["btn_sync_friend"] = "Sincronizza con Amico",
            ["btn_rock_on"] = "🎸 SPACCATUTTO!",
            ["btn_cancel"] = "✕ ANNULLA",
            ["btn_soundcheck"] = "🔊 Soundcheck",
            ["btn_autoheal"] = "🩺 Auto-Ripristino",
            ["btn_poster"] = "🖼️ Poster del Tour",
            ["btn_medley"] = "🎸 Medley",
            ["btn_clean_temp"] = "🧹 Pulisci Cache",
            ["btn_backup_now"] = "💾 Salva Backup",
            ["btn_restore_latest"] = "⏮ Ripristina Ultimo",
            ["btn_save_settings"] = "💾 Salva Impostazioni",
            ["stage_ready"] = "HUD PALCO PRONTO",
            ["stage_compiling"] = "COMPILAZIONE STEM AUDIO",
            ["stage_downloading"] = "TRASFERIMENTO AUDIO P2P",
            ["stage_complete"] = "BIS COMPLETATO!",
            ["opt_audio_studio"] = "Studio Audio & Volume",
            ["opt_controller_studio"] = "Studio Chitarra & Controller",
            ["opt_performance"] = "Motore & Prestazioni Windows 11",
            ["opt_security"] = "Scudo Zero-Trust & Quarantena",
            ["opt_tournament"] = "🏆 Torneo Tour Rock",
            ["opt_boss_battle"] = "⚡ Duello Boss"
        },
        ["ja"] = new()
        {
            ["nav_customs"] = "🎸 カスタム楽曲",
            ["nav_creator"] = "⚡ 楽曲クリエイター",
            ["nav_tools"] = "🛠 ツール＆音源分離",
            ["nav_friends"] = "👥 フレンド＆信頼",
            ["nav_p2p"] = "🌐 P2P同期＆ロビー",
            ["nav_import"] = "📦 インポート＆変換",
            ["nav_career"] = "💾 キャリアセーブ",
            ["nav_settings"] = "⚙ 設定",
            ["btn_play_gh3"] = "GH3で今すぐプレイ",
            ["btn_compile_song"] = "楽曲作成＆コンパイル",
            ["btn_sync_friend"] = "フレンドと同期",
            ["btn_rock_on"] = "🎸 ロックオン！",
            ["btn_cancel"] = "✕ キャンセル",
            ["btn_soundcheck"] = "🔊 サウンドチェック",
            ["btn_autoheal"] = "🩺 自動修復",
            ["btn_poster"] = "🖼️ ツアーポスター",
            ["btn_medley"] = "🎸 メドレー",
            ["btn_clean_temp"] = "🧹 キャッシュ削除",
            ["btn_backup_now"] = "💾 バックアップ作成",
            ["btn_restore_latest"] = "⏮ 最新を復元",
            ["btn_save_settings"] = "💾 設定を保存",
            ["stage_ready"] = "ステージHUD 準備完了",
            ["stage_compiling"] = "オーディオステムをコンパイル中",
            ["stage_downloading"] = "P2Pオーディオ転送中",
            ["stage_complete"] = "アンコール完了！",
            ["opt_audio_studio"] = "オーディオスタジオ＆ラウドネス",
            ["opt_controller_studio"] = "ギター＆コントローラースタジオ",
            ["opt_performance"] = "動作最適化＆Windows 11",
            ["opt_security"] = "ゼロトラスト＆隔離シールド",
            ["opt_tournament"] = "🏆 ロックツアートーナメント",
            ["opt_boss_battle"] = "⚡ ボスバトル対決"
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
            ["btn_soundcheck"] = "🔊 사운드체크",
            ["btn_autoheal"] = "🩺 자동 치료",
            ["btn_poster"] = "🖼️ 투어 포스터",
            ["btn_medley"] = "🎸 메들리",
            ["btn_clean_temp"] = "🧹 캐시 정리",
            ["btn_backup_now"] = "💾 스냅샷 백업",
            ["btn_restore_latest"] = "⏮ 최신본 복원",
            ["btn_save_settings"] = "💾 설정 저장",
            ["stage_ready"] = "스테이지 HUD 준비 완료",
            ["stage_compiling"] = "오디오 스템 컴파일 중",
            ["stage_downloading"] = "P2P 오디오 전송 중",
            ["stage_complete"] = "앙코르 완료!",
            ["opt_audio_studio"] = "오디오 스튜디오 및 음량 정규화",
            ["opt_controller_studio"] = "기타 및 컨트롤러 스튜디오",
            ["opt_performance"] = "엔진 및 Windows 11 최적화",
            ["opt_security"] = "제로 트러스트 및 격리 실드",
            ["opt_tournament"] = "🏆 락 투어 토너먼트",
            ["opt_boss_battle"] = "⚡ 보스 배틀 결투"
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
