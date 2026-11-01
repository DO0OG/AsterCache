/*
 * asterflt_proto.h -- AsterFlt 드라이버와 사용자 모드 서비스가 공유하는 메시지 구조체.
 *
 * 규칙: 고정 크기, 명시 정렬(8바이트), 포인터 없음. 드라이버(C)와 사용자 모드(C/C#)가
 * 같은 레이아웃을 쓰므로 필드를 바꾸면 ASTER_PROTO_VERSION 을 올립니다.
 * 통신: FilterConnectCommunicationPort(L"\AsterFltPort") 하나, FilterSendMessage 로
 *   요청-응답 (드라이버가 사용자 모드로 먼저 보내지 않음). 메시지 종류는 세 가지입니다.
 *     ASTER_CMD_SET_POLICY  정책 설정 (볼륨 GUID, 볼륨 상대 루트, 켜기/끄기)
 *     ASTER_CMD_GET_RECORDS 읽기 기록 묶음 가져오기 (가져간 만큼 링 버퍼에서 제거)
 *     ASTER_CMD_GET_STATS   통계
 * 이 헤더는 커널(ntddk/fltKernel.h 이후)과 사용자 모드(windows.h 이후)에서 포함할 수 있습니다.
 */
#ifndef ASTERFLT_PROTO_H
#define ASTERFLT_PROTO_H

#define ASTER_PROTO_VERSION    1UL
#define ASTER_PORT_NAME        L"\AsterFltPort"

#define ASTER_GUID_CCH         40   /* "{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}" 38자 + NUL, 2자 여유 */
#define ASTER_ROOT_CCH         260  /* 볼륨 상대 경로, 예: L"\Games\Foo" (NUL 포함) */
#define ASTER_MAX_BATCH        4096 /* 한 번에 가져올 수 있는 최대 기록 수 */

#define ASTER_CMD_SET_POLICY   1UL
#define ASTER_CMD_GET_RECORDS  2UL
#define ASTER_CMD_GET_STATS    3UL

/* ASTER_READ_RECORD.Flags */
#define ASTER_REC_PAGING_IO    0x00000001UL /* IRP_PAGING_IO */
#define ASTER_REC_NOCACHE      0x00000002UL /* IRP_NOCACHE */
#define ASTER_REC_FASTIO       0x00000004UL /* fast I/O 읽기 (캐시 적중 가능 경로) */
#define ASTER_REC_PID_UNKNOWN  0x00000008UL /* 요청 프로세스 ID 를 구하지 못함 (IRQL 이 높았음) */

#pragma pack(push, 8)

typedef struct _ASTER_COMMAND {
    unsigned long Version;   /* ASTER_PROTO_VERSION */
    unsigned long Command;   /* ASTER_CMD_* */
} ASTER_COMMAND;

/* ASTER_CMD_SET_POLICY 요청. 응답 없음. Arm=0 이면 관찰을 끔 (나머지 필드는 무시). */
typedef struct _ASTER_SET_POLICY {
    ASTER_COMMAND  Hdr;
    unsigned long  Arm;                        /* 0 = 끔, 1 = 켬 */
    unsigned long  Reserved;                   /* 0 */
    wchar_t        VolumeGuid[ASTER_GUID_CCH]; /* L"{GUID}" (NUL 종료) */
    wchar_t        Root[ASTER_ROOT_CCH];       /* 볼륨 상대 루트, '\' 로 시작, NUL 종료 */
} ASTER_SET_POLICY;

/* 읽기 한 건. 40바이트. */
typedef struct _ASTER_READ_RECORD {
    unsigned __int64 FileId;        /* NTFS 파일 참조 번호 (FileInternalInformation.IndexNumber) */
    __int64          ByteOffset;    /* 파일 내 오프셋 */
    unsigned __int64 InterruptTime; /* KeQueryInterruptTime, 100ns 단위 */
    unsigned long    Length;
    unsigned long    ProcessId;
    unsigned long    Flags;         /* ASTER_REC_* */
    unsigned long    Reserved;
} ASTER_READ_RECORD;

/* ASTER_CMD_GET_RECORDS 응답: 이 헤더 바로 뒤에 Count 개의 ASTER_READ_RECORD. */
typedef struct _ASTER_BATCH_HEADER {
    unsigned long    Count;
    unsigned long    Reserved;
    unsigned __int64 DroppedTotal;  /* 링 버퍼가 가득 차 버린 누적 수 */
} ASTER_BATCH_HEADER;

/* ASTER_CMD_GET_STATS 응답. */
typedef struct _ASTER_STATS {
    unsigned long    Version;
    unsigned long    Armed;
    unsigned long    PolicyGeneration;
    unsigned long    RingCapacity;   /* 기록 수 */
    unsigned __int64 Recorded;       /* 누적 기록 수 */
    unsigned __int64 Dropped;        /* 누적 버림 수 */
    unsigned __int64 Pending;        /* 지금 링에 남은 수 */
} ASTER_STATS;

#pragma pack(pop)

/* 크기 고정 확인 (C11 _Static_assert, MSVC 는 static_assert 매크로 대신 C_ASSERT 가 있으면 사용) */
typedef char aster_assert_record_40[(sizeof(ASTER_READ_RECORD) == 40) ? 1 : -1];
typedef char aster_assert_batch_16[(sizeof(ASTER_BATCH_HEADER) == 16) ? 1 : -1];
typedef char aster_assert_policy_616[(sizeof(ASTER_SET_POLICY) == 616) ? 1 : -1];
typedef char aster_assert_stats_40[(sizeof(ASTER_STATS) == 40) ? 1 : -1];

#endif /* ASTERFLT_PROTO_H */
