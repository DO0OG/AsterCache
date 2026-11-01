namespace AsterCache.Core;

/// 계획 검증 실패 (Python PlanRejected).
public sealed class PlanRejectedException : Exception
{
    public PlanRejectedException(string message) : base(message)
    {
    }
}

/// 관리자 권한 없음 (Python NotAdmin). CLI 는 종료 코드 3 으로 끝냅니다.
public sealed class NotAdminException : Exception
{
    public NotAdminException() : base("administrator privileges required")
    {
    }
}
