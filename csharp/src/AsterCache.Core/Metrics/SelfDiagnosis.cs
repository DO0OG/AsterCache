namespace AsterCache.Core.Metrics;

/// 첫 설치 셀프진단 (ZC-11) 틀: 항목과 결과 형식만. 실제 검사 코드는 넣지 않았습니다.
public enum DiagnosticItem { Policy, Profile, Permission, DiskError }

public enum DiagnosticStatus { Ok, Problem, Unknown }

public sealed record DiagnosticResult(DiagnosticItem Item, DiagnosticStatus Status, string Reason);
