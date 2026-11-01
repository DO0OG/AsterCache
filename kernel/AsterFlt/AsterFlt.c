/*
 * AsterFlt.c -- K1 관찰 전용 파일 시스템 미니필터 (PoC).
 *
 * 하는 일: 지정한 볼륨(GUID)의 지정한 루트 아래 파일에 대한 IRP_MJ_READ 를 Pre-Read 에서
 *          "기록만" 한다. IRP 를 바꾸거나 완료하지 않고 항상 FLT_PREOP_SUCCESS_NO_CALLBACK.
 * 하지 않는 일: 읽기 대체, 쓰기 관찰, 이름 변경/삭제 추적, 파일 내용 접근. (K3 는 설계 문서만)
 *
 * 흐름
 *   InstanceSetup  NTFS 로컬 디스크 볼륨에만 붙고, 볼륨 GUID 를 인스턴스 컨텍스트에 저장.
 *                  정책이 켜진 상태에서 GUID 가 다르면 붙지 않음 (STATUS_FLT_DO_NOT_ATTACH).
 *   PostCreate     (PASSIVE_LEVEL) 정책 루트 아래 파일이면 이름 조회는 여기서 딱 한 번 하고
 *                  파일 ID 와 정책 세대를 stream context 에 저장.
 *   PreRead        (<= DISPATCH_LEVEL) stream context 만 조회해 링 버퍼에 한 건 기록.
 *                  이름 조회, 페이지드 풀, 대기 호출 없음.
 *   통신 포트      연결 1개, Administrators/SYSTEM 전용. 사용자 모드가 FilterSendMessage 로 끌어감.
 *
 * 참고 문서
 *   FltRegisterFilter          https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fltkernel/nf-fltkernel-fltregisterfilter
 *   FLT_PREOP_CALLBACK_STATUS  https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fltkernel/ne-fltkernel-_flt_preop_callback_status
 *   FltCreateCommunicationPort https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fltkernel/nf-fltkernel-fltcreatecommunicationport
 *   Stream context             https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/managing-contexts-in-a-minifilter-driver
 *   FltAllocateContext         https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fltkernel/nf-fltkernel-fltallocatecontext
 *   FltGetVolumeGuidName       https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fltkernel/nf-fltkernel-fltgetvolumeguidname
 *   FltGetFileNameInformation  https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fltkernel/nf-fltkernel-fltgetfilenameinformation
 *   FltBuildDefaultSecurityDescriptor https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fltkernel/nf-fltkernel-fltbuilddefaultsecuritydescriptor
 *
 * 주의: 이 파일은 WDK 없이 작성되어 아직 컴파일되지 않았다. 각 IRQL 범위는 위 문서로
 * 첫 빌드 때 다시 대조할 것 (README 의 검토 체크리스트).
 */

#include <fltKernel.h>
#include <ntddk.h>
#include <wdm.h>
#include "inc\asterflt_proto.h"

#define ASTER_TAG_RING   'rtsA'
#define ASTER_TAG_CTX    'xtsA'
#define ASTER_RING_CAP   65536UL   /* 2의 거듭제곱. 65536 * 40 B = 2.5 MiB 비페이지 풀 */
#define ASTER_RING_MASK  (ASTER_RING_CAP - 1)
#define ASTER_MIN(a, b)  (((a) < (b)) ? (a) : (b))  /* wdm.h 의 min 매크로 유무에 기대지 않음 */
#define ASTER_GUID_NAME_BYTES  128 /* FltGetVolumeGuidName 결과 "\??\Volume{...}\" 는 약 98 바이트 */

/* ---- 컨텍스트 ---------------------------------------------------------- */

typedef struct _ASTER_INSTANCE_CTX {
    WCHAR VolumeGuid[ASTER_GUID_CCH];   /* "{GUID}" */
} ASTER_INSTANCE_CTX, *PASTER_INSTANCE_CTX;

typedef struct _ASTER_STREAM_CTX {
    ULONGLONG FileId;
    ULONG     Generation;               /* PostCreate 시점의 정책 세대. 다르면 낡은 것으로 보고 무시 */
} ASTER_STREAM_CTX, *PASTER_STREAM_CTX;

/* ---- 전역 상태 --------------------------------------------------------- */

typedef struct _ASTER_GLOBALS {
    PFLT_FILTER   Filter;
    PFLT_PORT     ServerPort;
    PFLT_PORT     ClientPort;           /* InterlockedExchangePointer 로만 바꿈 */

    /* 정책. Lock 으로 보호. PostCreate/InstanceSetup 은 스택으로 복사해서 쓴다. */
    KSPIN_LOCK    Lock;                 /* 정책, 링 Head/Tail, 통계 보호. 보유 중에는 복사뿐 */
    volatile LONG Armed;                /* 락 없이 읽는 빠른 검사용. 쓰기는 Lock 안에서 */
    volatile LONG Generation;           /* 정책이 바뀔 때마다 +1 (낡은 stream context 무효화) */
    WCHAR         PolicyGuid[ASTER_GUID_CCH];
    WCHAR         PolicyRoot[ASTER_ROOT_CCH];

    /* 링 버퍼 (고정 크기, 비페이지 풀). Head/Tail 은 단조 증가 카운터, 인덱스 = 값 & MASK.
     * 생산자는 Lock 안에서 Head 만 전진, 소비자는 Lock 안에서 Tail 만 전진한다. */
    ASTER_READ_RECORD *Ring;
    ULONG         Head;
    ULONG         Tail;
    ULONGLONG     Recorded;
    ULONGLONG     Dropped;
    volatile LONG Draining;             /* GET_RECORDS 동시 실행 방지 */

    EX_RUNDOWN_REF Rundown;             /* 메시지 콜백과 언로드 경합 */
} ASTER_GLOBALS;

static ASTER_GLOBALS g;

/* ---- 선언 -------------------------------------------------------------- */

DRIVER_INITIALIZE DriverEntry;

static NTSTATUS FLTAPI AsterUnload(_In_ FLT_FILTER_UNLOAD_FLAGS Flags);
static NTSTATUS FLTAPI AsterInstanceSetup(_In_ PCFLT_RELATED_OBJECTS FltObjects,
    _In_ FLT_INSTANCE_SETUP_FLAGS Flags, _In_ DEVICE_TYPE VolumeDeviceType,
    _In_ FLT_FILESYSTEM_TYPE VolumeFilesystemType);
static NTSTATUS FLTAPI AsterInstanceQueryTeardown(_In_ PCFLT_RELATED_OBJECTS FltObjects,
    _In_ FLT_INSTANCE_QUERY_TEARDOWN_FLAGS Flags);
static FLT_POSTOP_CALLBACK_STATUS FLTAPI AsterPostCreate(_Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects, _In_opt_ PVOID CompletionContext, _In_ FLT_POST_OPERATION_FLAGS Flags);
static FLT_PREOP_CALLBACK_STATUS FLTAPI AsterPreRead(_Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects, _Flt_CompletionContext_Outptr_ PVOID *CompletionContext);
static NTSTATUS FLTAPI AsterConnect(_In_ PFLT_PORT ClientPort, _In_opt_ PVOID ServerPortCookie,
    _In_reads_bytes_opt_(SizeOfContext) PVOID ConnectionContext, _In_ ULONG SizeOfContext,
    _Outptr_result_maybenull_ PVOID *ConnectionCookie);
static VOID FLTAPI AsterDisconnect(_In_opt_ PVOID ConnectionCookie);
static NTSTATUS FLTAPI AsterMessage(_In_opt_ PVOID PortCookie,
    _In_reads_bytes_opt_(InputBufferLength) PVOID InputBuffer, _In_ ULONG InputBufferLength,
    _Out_writes_bytes_to_opt_(OutputBufferLength, *ReturnOutputBufferLength) PVOID OutputBuffer,
    _In_ ULONG OutputBufferLength, _Out_ PULONG ReturnOutputBufferLength);

#ifdef ALLOC_PRAGMA
#pragma alloc_text(INIT, DriverEntry)
#pragma alloc_text(PAGE, AsterUnload)
#pragma alloc_text(PAGE, AsterInstanceSetup)
#pragma alloc_text(PAGE, AsterInstanceQueryTeardown)
#pragma alloc_text(PAGE, AsterPostCreate)
#pragma alloc_text(PAGE, AsterConnect)
#pragma alloc_text(PAGE, AsterDisconnect)
#pragma alloc_text(PAGE, AsterMessage)
#endif

/* ---- 링 버퍼 ------------------------------------------------------------ */

/* IRQL <= DISPATCH_LEVEL. Lock 안에서 복사만 한다. 가득 차면 버리고 Dropped 만 센다. */
static VOID AsterRingPush(_In_ const ASTER_READ_RECORD *Rec)
{
    KIRQL oldIrql;

    KeAcquireSpinLock(&g.Lock, &oldIrql);
    if ((ULONG)(g.Head - g.Tail) >= ASTER_RING_CAP) {
        g.Dropped++;
    } else {
        g.Ring[g.Head & ASTER_RING_MASK] = *Rec;
        g.Recorded++;
        g.Head++;               /* 스핀락이 순서를 보장: 소비자는 락 안에서 Head 를 읽음 */
    }
    KeReleaseSpinLock(&g.Lock, oldIrql);
}

/* ---- 정책 비교 도우미 --------------------------------------------------- */

typedef struct _ASTER_POLICY_SNAP {
    LONG  Armed;
    LONG  Generation;
    WCHAR Guid[ASTER_GUID_CCH];
    WCHAR Root[ASTER_ROOT_CCH];
} ASTER_POLICY_SNAP;     /* 약 0.6 KiB, 스택용 */

/* IRQL <= DISPATCH_LEVEL */
static VOID AsterSnapPolicy(_Out_ ASTER_POLICY_SNAP *Snap)
{
    KIRQL oldIrql;

    KeAcquireSpinLock(&g.Lock, &oldIrql);
    Snap->Armed = g.Armed;
    Snap->Generation = g.Generation;
    RtlCopyMemory(Snap->Guid, g.PolicyGuid, sizeof(Snap->Guid));
    RtlCopyMemory(Snap->Root, g.PolicyRoot, sizeof(Snap->Root));
    KeReleaseSpinLock(&g.Lock, oldIrql);
}

/* IRQL <= APC_LEVEL (RtlEqualUnicodeString 은 대소문자 비교에 페이지드 테이블을 쓸 수 있음).
 * 둘 다 NUL 종료 WCHAR 배열, 대소문자 무시 */
static BOOLEAN AsterGuidEqual(_In_ const WCHAR *A, _In_ const WCHAR *B)
{
    UNICODE_STRING a, b;

    RtlInitUnicodeString(&a, A);
    RtlInitUnicodeString(&b, B);
    return RtlEqualUnicodeString(&a, &b, TRUE);
}

/* ---- 인스턴스 ----------------------------------------------------------- */

/* IRQL PASSIVE_LEVEL (PAGED_CODE). 모든 경로에서 컨텍스트 참조와 임시 풀을 Cleanup 에서 해제 */
static NTSTATUS FLTAPI AsterInstanceSetup(
    _In_ PCFLT_RELATED_OBJECTS FltObjects, _In_ FLT_INSTANCE_SETUP_FLAGS Flags,
    _In_ DEVICE_TYPE VolumeDeviceType, _In_ FLT_FILESYSTEM_TYPE VolumeFilesystemType)
{
    NTSTATUS status;
    PASTER_INSTANCE_CTX ictx = NULL;
    UNICODE_STRING guidName = { 0, 0, NULL };
    ASTER_POLICY_SNAP snap;
    ULONG needed = 0;
    ULONG i, openIdx = (ULONG)-1, closeIdx = (ULONG)-1;

    UNREFERENCED_PARAMETER(Flags);
    PAGED_CODE();

    if (VolumeDeviceType != FILE_DEVICE_DISK_FILE_SYSTEM || VolumeFilesystemType != FLT_FSTYPE_NTFS) {
        return STATUS_FLT_DO_NOT_ATTACH;    /* 네트워크/RAW/ReFS 등 제외 (파일 ID 64비트 가정) */
    }

    guidName.MaximumLength = ASTER_GUID_NAME_BYTES;
    guidName.Buffer = ExAllocatePool2(POOL_FLAG_PAGED, ASTER_GUID_NAME_BYTES, ASTER_TAG_CTX);
    if (guidName.Buffer == NULL) {
        return STATUS_FLT_DO_NOT_ATTACH;
    }

    status = FltGetVolumeGuidName(FltObjects->Volume, &guidName, &needed);
    if (!NT_SUCCESS(status)) {
        goto Cleanup;
    }

    /* "\??\Volume{GUID}\" 에서 "{GUID}" 부분만 꺼낸다 */
    for (i = 0; i < guidName.Length / sizeof(WCHAR); i++) {
        if (guidName.Buffer[i] == L'{') openIdx = i;
        if (guidName.Buffer[i] == L'}') { closeIdx = i; break; }
    }
    if (openIdx == (ULONG)-1 || closeIdx == (ULONG)-1 || closeIdx < openIdx ||
        closeIdx - openIdx + 1 >= ASTER_GUID_CCH) {
        status = STATUS_FLT_DO_NOT_ATTACH;
        goto Cleanup;
    }

    status = FltAllocateContext(g.Filter, FLT_INSTANCE_CONTEXT, sizeof(ASTER_INSTANCE_CTX),
                                NonPagedPoolNx, (PFLT_CONTEXT *)&ictx);
    if (!NT_SUCCESS(status)) {
        goto Cleanup;
    }
    RtlZeroMemory(ictx, sizeof(*ictx));
    RtlCopyMemory(ictx->VolumeGuid, &guidName.Buffer[openIdx], (closeIdx - openIdx + 1) * sizeof(WCHAR));

    /* 정책이 이미 켜져 있고 볼륨이 다르면 붙지 않는다. 꺼져 있으면 일단 붙고 PostCreate 에서 거른다 */
    AsterSnapPolicy(&snap);
    if (snap.Armed && !AsterGuidEqual(snap.Guid, ictx->VolumeGuid)) {
        status = STATUS_FLT_DO_NOT_ATTACH;
        goto Cleanup;
    }

    status = FltSetInstanceContext(FltObjects->Instance, FLT_SET_CONTEXT_KEEP_IF_EXISTS, ictx, NULL);

Cleanup:
    if (ictx != NULL) {
        FltReleaseContext(ictx);            /* Set 성공 시 인스턴스가 참조를 유지 */
    }
    if (guidName.Buffer != NULL) {
        ExFreePoolWithTag(guidName.Buffer, ASTER_TAG_CTX);
    }
    return NT_SUCCESS(status) ? STATUS_SUCCESS : STATUS_FLT_DO_NOT_ATTACH;
}

static NTSTATUS FLTAPI AsterInstanceQueryTeardown(
    _In_ PCFLT_RELATED_OBJECTS FltObjects, _In_ FLT_INSTANCE_QUERY_TEARDOWN_FLAGS Flags)
{
    UNREFERENCED_PARAMETER(FltObjects);
    UNREFERENCED_PARAMETER(Flags);
    PAGED_CODE();
    return STATUS_SUCCESS;                  /* 수동 분리(fltmc detach) 허용 */
}

/* ---- PostCreate: 이름 조회는 여기서 한 번만 ------------------------------ */

/*
 * IRQL PASSIVE_LEVEL 가정 (IRP_MJ_CREATE 의 post-op 은 요청 스레드 문맥. 첫 빌드 때 문서로 재확인).
 * 항상 FLT_POSTOP_FINISHED_PROCESSING. 실패하면 컨텍스트를 만들지 않을 뿐 파일 열기는 건드리지 않음.
 */
static FLT_POSTOP_CALLBACK_STATUS FLTAPI AsterPostCreate(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _In_opt_ PVOID CompletionContext, _In_ FLT_POST_OPERATION_FLAGS Flags)
{
    ASTER_POLICY_SNAP snap;
    PASTER_INSTANCE_CTX ictx = NULL;
    PASTER_STREAM_CTX sctx = NULL;
    PFLT_FILE_NAME_INFORMATION nameInfo = NULL;
    FILE_INTERNAL_INFORMATION fileInfo;
    UNICODE_STRING rel, root;
    BOOLEAN isDir = FALSE;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(CompletionContext);
    PAGED_CODE();

    if (FlagOn(Flags, FLTFL_POST_OPERATION_DRAINING) ||
        !NT_SUCCESS(Data->IoStatus.Status) || Data->IoStatus.Status == STATUS_REPARSE ||
        Data->RequestorMode == KernelMode) {
        return FLT_POSTOP_FINISHED_PROCESSING;
    }
    if (!g.Armed) {
        return FLT_POSTOP_FINISHED_PROCESSING;
    }

    status = FltGetInstanceContext(FltObjects->Instance, (PFLT_CONTEXT *)&ictx);
    if (!NT_SUCCESS(status)) {
        return FLT_POSTOP_FINISHED_PROCESSING;
    }

    AsterSnapPolicy(&snap);
    if (!snap.Armed || !AsterGuidEqual(snap.Guid, ictx->VolumeGuid)) {
        goto Done;
    }

    status = FltIsDirectory(FltObjects->FileObject, FltObjects->Instance, &isDir);
    if (!NT_SUCCESS(status) || isDir) {
        goto Done;
    }

    status = FltGetFileNameInformation(Data, FLT_FILE_NAME_NORMALIZED | FLT_FILE_NAME_QUERY_DEFAULT, &nameInfo);
    if (!NT_SUCCESS(status)) {
        goto Done;
    }
    status = FltParseFileNameInformation(nameInfo);
    if (!NT_SUCCESS(status) || nameInfo->Name.Length < nameInfo->Volume.Length) {
        goto Done;
    }

    /* "\Device\HarddiskVolumeN\Games\Foo\a.bin" -> "\Games\Foo\a.bin" */
    rel.Buffer = (PWCH)((PUCHAR)nameInfo->Name.Buffer + nameInfo->Volume.Length);
    rel.Length = nameInfo->Name.Length - nameInfo->Volume.Length;
    rel.MaximumLength = rel.Length;
    RtlInitUnicodeString(&root, snap.Root);
    if (root.Length > sizeof(WCHAR) && root.Buffer[root.Length / sizeof(WCHAR) - 1] == L'\\') {
        root.Length -= sizeof(WCHAR);                       /* 끝의 '\' 는 무시 ("\" 단독은 유지) */
    }
    if (!RtlPrefixUnicodeString(&root, &rel, TRUE)) {
        goto Done;
    }
    /* 경계 확인: "\Games\Foo" 가 "\Games\Foobar" 와 맞지 않도록. 루트가 "\" 이면 통과 */
    if (root.Length > sizeof(WCHAR) && rel.Length > root.Length &&
        rel.Buffer[root.Length / sizeof(WCHAR)] != L'\\' &&
        rel.Buffer[root.Length / sizeof(WCHAR)] != L':') {
        goto Done;
    }

    status = FltQueryInformationFile(FltObjects->Instance, FltObjects->FileObject,
                                     &fileInfo, sizeof(fileInfo), FileInternalInformation, NULL);
    if (!NT_SUCCESS(status)) {
        goto Done;
    }

    status = FltAllocateContext(g.Filter, FLT_STREAM_CONTEXT, sizeof(ASTER_STREAM_CTX),
                                NonPagedPoolNx, (PFLT_CONTEXT *)&sctx);
    if (!NT_SUCCESS(status)) {
        goto Done;
    }
    sctx->FileId = (ULONGLONG)fileInfo.IndexNumber.QuadPart;
    sctx->Generation = (ULONG)snap.Generation;
    /* 이미 있으면 교체 (낡은 세대 값을 새 정책으로 갱신). 이전 컨텍스트 반환은 필요 없음 */
    (VOID)FltSetStreamContext(FltObjects->Instance, FltObjects->FileObject,
                              FLT_SET_CONTEXT_REPLACE_IF_EXISTS, sctx, NULL);

Done:
    if (sctx != NULL) FltReleaseContext(sctx);
    if (nameInfo != NULL) FltReleaseFileNameInformation(nameInfo);
    if (ictx != NULL) FltReleaseContext(ictx);
    return FLT_POSTOP_FINISHED_PROCESSING;
}

/* ---- Pre-Read: 관찰만 ----------------------------------------------------- */

/*
 * IRQL <= DISPATCH_LEVEL (fast I/O 와 페이징 경로 포함). 금지: 이름 조회, 페이지드 풀, 대기, IRP 수정.
 * 쓰는 것: FltGetStreamContext(<= DISPATCH), KeQueryInterruptTime(모든 IRQL), 스핀락 링 버퍼.
 * 반환은 언제나 FLT_PREOP_SUCCESS_NO_CALLBACK (Post-Read 를 요청하지 않음).
 */
static FLT_PREOP_CALLBACK_STATUS FLTAPI AsterPreRead(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _Flt_CompletionContext_Outptr_ PVOID *CompletionContext)
{
    PASTER_STREAM_CTX sctx = NULL;
    ASTER_READ_RECORD rec;
    ULONG length;
    ULONG iflags;

    *CompletionContext = NULL;

    if (!g.Armed || FltObjects->FileObject == NULL) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }
    length = Data->Iopb->Parameters.Read.Length;
    if (length == 0) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }
    if (!NT_SUCCESS(FltGetStreamContext(FltObjects->Instance, FltObjects->FileObject, (PFLT_CONTEXT *)&sctx))) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;       /* 루트 밖이거나 필터 적재 전에 열린 파일 */
    }

    if ((LONG)sctx->Generation == g.Generation) {
        iflags = Data->Iopb->IrpFlags;
        rec.FileId = sctx->FileId;
        rec.ByteOffset = Data->Iopb->Parameters.Read.ByteOffset.QuadPart;
        rec.InterruptTime = KeQueryInterruptTime();
        rec.Length = length;
        rec.Flags = 0;
        if (iflags & IRP_PAGING_IO) rec.Flags |= ASTER_REC_PAGING_IO;
        if (iflags & IRP_NOCACHE)   rec.Flags |= ASTER_REC_NOCACHE;
        if (FLT_IS_FASTIO_OPERATION(Data)) rec.Flags |= ASTER_REC_FASTIO;
        /* FltGetRequestorProcessId 의 IRQL 상한은 문서로 재확인 필요. 보수적으로 APC_LEVEL 이하에서만 호출 */
        if (KeGetCurrentIrql() <= APC_LEVEL) {
            rec.ProcessId = FltGetRequestorProcessId(Data);
        } else {
            rec.ProcessId = 0;
            rec.Flags |= ASTER_REC_PID_UNKNOWN;
        }
        rec.Reserved = 0;
        AsterRingPush(&rec);
    }
    FltReleaseContext(sctx);
    return FLT_PREOP_SUCCESS_NO_CALLBACK;
}

/* ---- 통신 포트 ------------------------------------------------------------ */

/* IRQL PASSIVE_LEVEL. 연결은 1개만 (FltCreateCommunicationPort MaxConnections=1) */
static NTSTATUS FLTAPI AsterConnect(
    _In_ PFLT_PORT ClientPort, _In_opt_ PVOID ServerPortCookie,
    _In_reads_bytes_opt_(SizeOfContext) PVOID ConnectionContext, _In_ ULONG SizeOfContext,
    _Outptr_result_maybenull_ PVOID *ConnectionCookie)
{
    UNREFERENCED_PARAMETER(ServerPortCookie);
    UNREFERENCED_PARAMETER(ConnectionContext);
    UNREFERENCED_PARAMETER(SizeOfContext);
    PAGED_CODE();

    *ConnectionCookie = NULL;
    if (!ExAcquireRundownProtection(&g.Rundown)) {
        return STATUS_SHUTDOWN_IN_PROGRESS;
    }
    if (InterlockedCompareExchangePointer((PVOID *)&g.ClientPort, ClientPort, NULL) != NULL) {
        ExReleaseRundownProtection(&g.Rundown);
        return STATUS_ALREADY_REGISTERED;
    }
    ExReleaseRundownProtection(&g.Rundown);
    return STATUS_SUCCESS;
}

/* IRQL PASSIVE_LEVEL. 끊기면 관찰을 끄고 세대를 올려 stream context 를 낡게 만든다 */
static VOID FLTAPI AsterDisconnect(_In_opt_ PVOID ConnectionCookie)
{
    PFLT_PORT port;
    KIRQL oldIrql;

    UNREFERENCED_PARAMETER(ConnectionCookie);
    PAGED_CODE();

    KeAcquireSpinLock(&g.Lock, &oldIrql);
    g.Armed = 0;
    g.Generation++;
    KeReleaseSpinLock(&g.Lock, oldIrql);

    port = (PFLT_PORT)InterlockedExchangePointer((PVOID *)&g.ClientPort, NULL);
    if (port != NULL && g.Filter != NULL) {   /* 언로드가 Filter 를 비운 뒤 도착한 끊김은 포트를 닫을 수 없음: VM 에서 확인 */
        FltCloseClientPort(g.Filter, &port);
    }
}

/* PASSIVE_LEVEL. 사용자 버퍼에서 커널 스택으로 복사. 실패하면 STATUS_INVALID_USER_BUFFER */
static NTSTATUS AsterCopyIn(_Out_writes_bytes_(Len) PVOID Dst, _In_ PVOID Src, _In_ ULONG Len)
{
    __try {
        ProbeForRead(Src, Len, sizeof(UCHAR));
        RtlCopyMemory(Dst, Src, Len);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return STATUS_INVALID_USER_BUFFER;
    }
    return STATUS_SUCCESS;
}

/* PASSIVE_LEVEL */
static NTSTATUS AsterDoSetPolicy(_In_ const ASTER_SET_POLICY *P)
{
    KIRQL oldIrql;
    ULONG i;
    BOOLEAN okGuid = FALSE, okRoot = FALSE;

    if (P->Arm != 0) {
        /* 입력 검증: GUID 는 "{" 로 시작해 "}" 로 끝나는 38자 NUL 종료, 루트는 '\' 로 시작 NUL 종료 */
        for (i = 0; i < ASTER_GUID_CCH; i++) {
            if (P->VolumeGuid[i] == L'\0') {
                okGuid = (i == 38 && P->VolumeGuid[0] == L'{' && P->VolumeGuid[37] == L'}');
                break;
            }
        }
        for (i = 0; i < ASTER_ROOT_CCH; i++) {
            if (P->Root[i] == L'\0') {
                okRoot = (i >= 1 && P->Root[0] == L'\\');
                break;
            }
        }
        if (!okGuid || !okRoot) {
            return STATUS_INVALID_PARAMETER;
        }
    }

    KeAcquireSpinLock(&g.Lock, &oldIrql);
    g.Generation++;                                     /* 이전 정책의 stream context 무효화 */
    if (P->Arm != 0) {
        RtlCopyMemory(g.PolicyGuid, P->VolumeGuid, sizeof(g.PolicyGuid));
        RtlCopyMemory(g.PolicyRoot, P->Root, sizeof(g.PolicyRoot));
        g.Armed = 1;
    } else {
        g.Armed = 0;
        RtlZeroMemory(g.PolicyGuid, sizeof(g.PolicyGuid));
        RtlZeroMemory(g.PolicyRoot, sizeof(g.PolicyRoot));
    }
    KeReleaseSpinLock(&g.Lock, oldIrql);
    return STATUS_SUCCESS;
}

/* PASSIVE_LEVEL. 링에서 최대 maxFit 건을 사용자 버퍼로 복사하고 Tail 을 전진 (복사 성공 뒤에만) */
static NTSTATUS AsterDoGetRecords(_Out_ PVOID Out, _In_ ULONG OutLen, _Out_ PULONG Returned)
{
    ULONG head, tail, avail, maxFit, n, chunk, i;
    ASTER_BATCH_HEADER hdr;
    ULONGLONG dropped;
    KIRQL oldIrql;
    NTSTATUS status = STATUS_SUCCESS;
    PUCHAR dst;

    *Returned = 0;
    if (OutLen < sizeof(ASTER_BATCH_HEADER) || ((ULONG_PTR)Out & 7) != 0) {
        return STATUS_BUFFER_TOO_SMALL;
    }
    if (InterlockedCompareExchange(&g.Draining, 1, 0) != 0) {
        return STATUS_DEVICE_BUSY;
    }

    KeAcquireSpinLock(&g.Lock, &oldIrql);
    head = g.Head;                  /* Head 이전의 레코드는 모두 완성돼 있음 (Lock 안에서 쓰고 증가) */
    tail = g.Tail;                  /* Tail 은 이 함수(Draining 보호)만 바꿈 */
    dropped = g.Dropped;
    KeReleaseSpinLock(&g.Lock, oldIrql);

    avail = head - tail;
    maxFit = (OutLen - (ULONG)sizeof(ASTER_BATCH_HEADER)) / (ULONG)sizeof(ASTER_READ_RECORD);
    n = ASTER_MIN(ASTER_MIN(avail, maxFit), ASTER_MAX_BATCH);

    hdr.Count = n;
    hdr.Reserved = 0;
    hdr.DroppedTotal = dropped;
    dst = (PUCHAR)Out;
    __try {
        ProbeForWrite(Out, sizeof(ASTER_BATCH_HEADER) + (SIZE_T)n * sizeof(ASTER_READ_RECORD), 8);
        RtlCopyMemory(dst, &hdr, sizeof(hdr));
        dst += sizeof(hdr);
        for (i = 0; i < n; i += chunk) {   /* 링 끝에서 감기는 구간은 두 번에 나눠 복사 */
            chunk = ASTER_MIN(n - i, ASTER_RING_CAP - ((tail + i) & ASTER_RING_MASK));
            RtlCopyMemory(dst, &g.Ring[(tail + i) & ASTER_RING_MASK], (SIZE_T)chunk * sizeof(ASTER_READ_RECORD));
            dst += (SIZE_T)chunk * sizeof(ASTER_READ_RECORD);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        status = STATUS_INVALID_USER_BUFFER;
    }

    if (NT_SUCCESS(status)) {
        KeAcquireSpinLock(&g.Lock, &oldIrql);   /* 생산자가 가득 참 판정에 읽는 Tail 이라 Lock 안에서 갱신 */
        g.Tail = tail + n;
        KeReleaseSpinLock(&g.Lock, oldIrql);
        *Returned = (ULONG)sizeof(ASTER_BATCH_HEADER) + n * (ULONG)sizeof(ASTER_READ_RECORD);
    }
    InterlockedExchange(&g.Draining, 0);
    return status;
}

/* PASSIVE_LEVEL */
static NTSTATUS AsterDoGetStats(_Out_ PVOID Out, _In_ ULONG OutLen, _Out_ PULONG Returned)
{
    ASTER_STATS st;
    KIRQL oldIrql;

    *Returned = 0;
    if (OutLen < sizeof(ASTER_STATS) || ((ULONG_PTR)Out & 7) != 0) {
        return STATUS_BUFFER_TOO_SMALL;
    }
    KeAcquireSpinLock(&g.Lock, &oldIrql);
    st.Version = ASTER_PROTO_VERSION;
    st.Armed = (ULONG)g.Armed;
    st.PolicyGeneration = (ULONG)g.Generation;
    st.RingCapacity = ASTER_RING_CAP;
    st.Recorded = g.Recorded;
    st.Dropped = g.Dropped;
    st.Pending = (ULONG)(g.Head - g.Tail);
    KeReleaseSpinLock(&g.Lock, oldIrql);

    __try {
        ProbeForWrite(Out, sizeof(st), 8);
        RtlCopyMemory(Out, &st, sizeof(st));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return STATUS_INVALID_USER_BUFFER;
    }
    *Returned = (ULONG)sizeof(st);
    return STATUS_SUCCESS;
}

/* IRQL PASSIVE_LEVEL. 입력/출력 버퍼는 사용자 주소이므로 반드시 __try 로 접근 */
static NTSTATUS FLTAPI AsterMessage(
    _In_opt_ PVOID PortCookie,
    _In_reads_bytes_opt_(InputBufferLength) PVOID InputBuffer, _In_ ULONG InputBufferLength,
    _Out_writes_bytes_to_opt_(OutputBufferLength, *ReturnOutputBufferLength) PVOID OutputBuffer,
    _In_ ULONG OutputBufferLength, _Out_ PULONG ReturnOutputBufferLength)
{
    ASTER_COMMAND cmd;
    ASTER_SET_POLICY pol;                               /* 약 0.6 KiB 스택 */
    NTSTATUS status;

    UNREFERENCED_PARAMETER(PortCookie);
    PAGED_CODE();

    *ReturnOutputBufferLength = 0;
    if (InputBuffer == NULL || InputBufferLength < sizeof(ASTER_COMMAND)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (!ExAcquireRundownProtection(&g.Rundown)) {
        return STATUS_SHUTDOWN_IN_PROGRESS;             /* 언로드 진행 중 */
    }

    status = AsterCopyIn(&cmd, InputBuffer, sizeof(cmd));
    if (NT_SUCCESS(status) && cmd.Version != ASTER_PROTO_VERSION) {
        status = STATUS_REVISION_MISMATCH;
    }
    if (NT_SUCCESS(status)) {
        switch (cmd.Command) {
        case ASTER_CMD_SET_POLICY:
            if (InputBufferLength < sizeof(pol)) {
                status = STATUS_INVALID_PARAMETER;
                break;
            }
            status = AsterCopyIn(&pol, InputBuffer, sizeof(pol));
            if (NT_SUCCESS(status)) {
                status = AsterDoSetPolicy(&pol);
            }
            break;
        case ASTER_CMD_GET_RECORDS:
            status = OutputBuffer ? AsterDoGetRecords(OutputBuffer, OutputBufferLength, ReturnOutputBufferLength)
                                  : STATUS_INVALID_PARAMETER;
            break;
        case ASTER_CMD_GET_STATS:
            status = OutputBuffer ? AsterDoGetStats(OutputBuffer, OutputBufferLength, ReturnOutputBufferLength)
                                  : STATUS_INVALID_PARAMETER;
            break;
        default:
            status = STATUS_INVALID_PARAMETER;
            break;
        }
    }
    ExReleaseRundownProtection(&g.Rundown);
    return status;
}

/* ---- 등록과 언로드 ---------------------------------------------------------- */

static const FLT_CONTEXT_REGISTRATION g_Contexts[] = {
    { FLT_INSTANCE_CONTEXT, 0, NULL, sizeof(ASTER_INSTANCE_CTX), ASTER_TAG_CTX, NULL, NULL, NULL },
    { FLT_STREAM_CONTEXT,   0, NULL, sizeof(ASTER_STREAM_CTX),   ASTER_TAG_CTX, NULL, NULL, NULL },
    { FLT_CONTEXT_END }
};

static const FLT_OPERATION_REGISTRATION g_Callbacks[] = {
    { IRP_MJ_CREATE, 0, NULL,         AsterPostCreate },
    { IRP_MJ_READ,   0, AsterPreRead, NULL },       /* 페이징·fast I/O 도 받음 (SKIP 플래그 없음) */
    { IRP_MJ_OPERATION_END }
};

static const FLT_REGISTRATION g_Registration = {
    sizeof(FLT_REGISTRATION), FLT_REGISTRATION_VERSION, 0,
    g_Contexts, g_Callbacks,
    AsterUnload, AsterInstanceSetup, AsterInstanceQueryTeardown,
    NULL, NULL, NULL, NULL, NULL, NULL, NULL
};

/*
 * IRQL PASSIVE_LEVEL. 순서가 중요하다.
 *  1) 클라이언트가 붙어 있으면 (강제 언로드가 아닌 한) 거부: 포트 정리 경합 회피.
 *  2) 서버 포트를 닫아 새 연결 차단 -> 정책 끔 -> Rundown 대기로 진행 중인 메시지 콜백 종료.
 *  3) FltUnregisterFilter 가 돌아오면 Pre/Post 콜백도 더는 없다 -> 그다음에야 링 버퍼 해제.
 */
static NTSTATUS FLTAPI AsterUnload(_In_ FLT_FILTER_UNLOAD_FLAGS Flags)
{
    KIRQL oldIrql;

    PAGED_CODE();

    if (g.ClientPort != NULL && !FlagOn(Flags, FLTFL_FILTER_UNLOAD_MANDATORY)) {
        return STATUS_FLT_DO_NOT_DETACH;
    }

    FltCloseCommunicationPort(g.ServerPort);
    g.ServerPort = NULL;

    KeAcquireSpinLock(&g.Lock, &oldIrql);
    g.Armed = 0;
    g.Generation++;
    KeReleaseSpinLock(&g.Lock, oldIrql);

    ExWaitForRundownProtectionRelease(&g.Rundown);

    FltUnregisterFilter(g.Filter);
    g.Filter = NULL;

    ExFreePoolWithTag(g.Ring, ASTER_TAG_RING);
    g.Ring = NULL;
    return STATUS_SUCCESS;
}

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    NTSTATUS status;
    PSECURITY_DESCRIPTOR sd = NULL;
    OBJECT_ATTRIBUTES oa;
    UNICODE_STRING portName;

    UNREFERENCED_PARAMETER(RegistryPath);

    RtlZeroMemory(&g, sizeof(g));
    KeInitializeSpinLock(&g.Lock);
    ExInitializeRundownProtection(&g.Rundown);

    g.Ring = (ASTER_READ_RECORD *)ExAllocatePool2(POOL_FLAG_NON_PAGED,
                                                  (SIZE_T)ASTER_RING_CAP * sizeof(ASTER_READ_RECORD), ASTER_TAG_RING);
    if (g.Ring == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    status = FltRegisterFilter(DriverObject, &g_Registration, &g.Filter);
    if (!NT_SUCCESS(status)) {
        goto Fail;
    }

    status = FltBuildDefaultSecurityDescriptor(&sd, FLT_PORT_ALL_ACCESS);   /* Administrators + SYSTEM */
    if (!NT_SUCCESS(status)) {
        goto FailUnregister;
    }
    RtlInitUnicodeString(&portName, ASTER_PORT_NAME);
    InitializeObjectAttributes(&oa, &portName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, sd);
    status = FltCreateCommunicationPort(g.Filter, &g.ServerPort, &oa, NULL,
                                        AsterConnect, AsterDisconnect, AsterMessage, 1);
    FltFreeSecurityDescriptor(sd);
    if (!NT_SUCCESS(status)) {
        goto FailUnregister;
    }

    status = FltStartFiltering(g.Filter);
    if (!NT_SUCCESS(status)) {
        FltCloseCommunicationPort(g.ServerPort);
        g.ServerPort = NULL;
        goto FailUnregister;
    }
    return STATUS_SUCCESS;

FailUnregister:
    FltUnregisterFilter(g.Filter);
    g.Filter = NULL;
Fail:
    ExFreePoolWithTag(g.Ring, ASTER_TAG_RING);
    g.Ring = NULL;
    return status;
}
