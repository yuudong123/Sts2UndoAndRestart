using MegaCrit.Sts2.Core.Localization;

namespace UndoAndRedoForkCode;

internal static class UndoText
{
    public static bool IsKorean => string.Equals(LocManager.Instance?.Language, "kor", StringComparison.OrdinalIgnoreCase);
    public static bool IsSimplifiedChinese => string.Equals(LocManager.Instance?.Language, "zhs", StringComparison.OrdinalIgnoreCase);

    public static string Pick(string korean, string english)
    {
        return IsKorean ? korean : english;
    }

    public static string Pick(string korean, string english, string simplifiedChinese)
    {
        if (IsKorean)
        {
            return korean;
        }

        return IsSimplifiedChinese ? simplifiedChinese : english;
    }

    public static string ActionHistory => Pick("사용 기록", "Action History");

    public static string Close => Pick("닫기", "Close");

    public static string SnapshotLimitTitle => Pick("스냅샷 최대 개수", "Maximum Snapshots");

    public static string LimitRangeText => Pick("최소값 없음", "No minimum");

    public static string SnapshotLimitHint => Pick(
        $"범위: {LimitRangeText}. 저장 후 다음 스냅샷부터 적용.",
        $"Range: {LimitRangeText}. Applies to new snapshots after saving.");

    public static string SnapshotLimitWarning => Pick(
        "경고: 수치를 너무 크게 잡으면 긴 전투에서 메모리 사용량과 UI 갱신 비용이 커질 수 있음.",
        "Warning: very high values can increase memory usage and UI refresh cost in long combats.");

    public static string ShowHistoryTab => Pick("우상단 사용 기록 탭 표시", "Show top-right action history tab");

    public static string Save => Pick("저장", "Save");

    public static string Saved(int value) => Pick($"저장됨: {value}", $"Saved: {value}");

    public static string NumberOnly => Pick("숫자만 입력해줘.", "Enter numbers only.");

    public static string InputUndo => Pick("되돌리기", "Undo");

    public static string InputRedo => Pick("다시실행", "Redo");

    public static string InputRestart => Pick("층 다시 시작", "Restart Floor");

    public static string NextTurnStart => Pick("다음 턴 시작", "Next Turn Start");

    public static string Turn(int turnNumber) => Pick($"턴 {Math.Max(1, turnNumber)}", $"Turn {Math.Max(1, turnNumber)}");

    public static string NoTarget => Pick("대상 없음", "No Target");

    public static string DiscardSlot(uint slotIndex) => Pick($"{slotIndex + 1}번 슬롯 버림", $"Discard slot {slotIndex + 1}");

    public static string RestartCombat => Pick("전투 다시 시작 (F5)", "Restart Combat (F5)", "重新开始战斗 (F5)");

    public static string RestartCombatTooltip => Pick(
        "현재 전투/방을 F5처럼 처음부터 다시 시작",
        "Restart the current combat/room from the beginning, like pressing F5.",
        "像按 F5 一样，从头重新开始当前战斗/房间。");

    public static string InitialState(bool current)
    {
        return current
            ? Pick("> 처음 상태 <", "> Initial State <")
            : Pick("처음 상태로 돌아가기 (기록 보존)", "Return to Initial State (Keep History)");
    }

    public static string InitialStateTooltip => Pick(
        "아무것도 사용하기 전의 처음 상태로 돌아감",
        "Return to the initial state before any action was used.");

    public static string Snapshot(int snapshotIndex) => Pick($"스냅샷 {snapshotIndex + 1}", $"Snapshot {snapshotIndex + 1}");

    public static string Redo => Pick("재실행", "Redo");

    public static string Kind(UndoStackEntryKind kind)
    {
        return kind switch
        {
            UndoStackEntryKind.Card => Pick("카드", "Card"),
            UndoStackEntryKind.Potion => Pick("물약", "Potion"),
            UndoStackEntryKind.DiscardPotion => Pick("버림", "Discard"),
            UndoStackEntryKind.TurnTransition => Pick("턴", "Turn"),
            _ => Pick("행동", "Action"),
        };
    }
}
