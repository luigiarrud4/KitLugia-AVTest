# Reverse Engineering: Everything 1.4.1.1032 (x64)

## Alvo
- Path: `C:/Users/Lugia/Downloads/Everything-1.4.1.1032.x64/Everything.exe`
- SHA256: `f191f756996a14a11e5445fa7103d302efd510cf2fbf920e6c0c8ed51d512e36`
- Formato: PE64 (x86_64), GUI app (subsystem Windows)
- Tamanho: ~2.2 MB (2.272.424 bytes)
- Analisado com IDA Pro 9.3 (idat headless, modo batch) → `Everything.exe.i64` + `Everything.exe.asm` (28.7 MB)
- Funções: 2616 (sub_*) + CRT. Strings: ~tens de milhares.

## Arquitetura inferida
Everything é um indexador de filesystem Windows com múltiplos modos de operação:
1. **GUI principal** (janela principal, listview, busca incremental).
2. **Serviço Windows** ("Everything Service") via named pipe `\\.\PIPE\Everything Service` e SCM (`CreateServiceW`, `StartServiceCtrlDispatcherW`, `RegisterServiceCtrlHandlerW`, `SetServiceStatus`).
3. **Servidor HTTP** ("Everything HTTP Server") e **ETP/FTP** (rede).
4. **ES: URLs** handler ("Open \"Everything\" from ES: URLs.").
5. **Shell integration** (drag/drop, clipboard, menus de contexto, hotkeys).

## Imports relevantes (686 símbolos únicos)
- **Rede**: `socket`, `bind`, `listen`, `accept`, `connect`, `send`/`recv`, `WSAStartup`, `WSAAsyncSelect`, `htons`, `inet_addr`, `gethostbyname`, `getpeername`, `getsockname`, `setsockopt`, `shutdown`, `closesocket`, `WSACleanup`, `WSAGetLastError`.
- **Serviço Windows**: `OpenSCManagerW`, `OpenServiceW`, `CreateServiceW`, `StartServiceW`, `ControlService`, `DeleteService`, `QueryServiceConfigW`, `StartServiceCtrlDispatcherW`, `RegisterServiceCtrlHandlerW`, `SetServiceStatus`, `CloseServiceHandle`.
- **IPC/Named pipe**: `CreateNamedPipeW`, `ConnectNamedPipe`, `CreateMutexW`.
- **Filesystem**: `FindFirstFileW/FindNextFileW/FindClose`, `FindFirstChangeNotificationW`, `FindNextChangeNotification`, `FindCloseChangeNotification`, `CreateFileW`, `ReadFile`, `WriteFile`, `CopyFileW`, `MoveFileW/MoveFileExW`, `DeleteFileW`, `RemoveDirectoryW`, `CreateDirectoryW`, `GetFileAttributesW/ExW`, `GetFileInformationByHandle`, `GetFileSize`, `SetFilePointer`, `FlushFileBuffers`, `GetFullPathNameW`, `QueryDosDeviceW`, `GetDriveTypeW`, `GetVolumeInformationW`, `GetDiskFreeSpaceW`.
- **Registry**: `RegOpenKeyExW`, `RegQueryValueExW`, `RegSetValueExW`, `RegCreateKeyExW`, `RegDeleteKeyW`, `RegDeleteValueW`, `RegEnumKeyW`, `RegCloseKey`, `SHRegGetUSValueW`.
- **COM/OLE/Shell**: `CoInitializeEx`, `CoCreateInstance`, `CoTaskMemAlloc/Free`, `OleInitialize/Uninitialize`, `DoDragDrop`, `RegisterDragDrop`, `RevokeDragDrop`, `DragAcceptFiles`, `DragQueryFileW`, `ShellExecuteExW`, `SHFileOperationW`, `SHChangeNotify`, `SHGetFileInfoW`, `SHGetPathFromIDListW`, `SHBrowseForFolderW`, `Shell_NotifyIconW`.
- **UI (Win32 + Common Controls)**: `CreateWindowExW`, `DefWindowProcW`, `RegisterClassExW`, `DialogBoxIndirectParamW`, `CreateDialogIndirectParamW`, `SendMessageW`, `PostMessageW`, `DispatchMessageW`, `TranslateMessage`, `PeekMessageW`, `GetMessageW`, `SetTimer`/`KillTimer`, `TrackPopupMenu`, `AppendMenuW`, `InsertMenuW`, `EnableMenuItem`, `SetMenuItemInfoW`, `GetOpenFileNameW`/`GetSaveFileNameW` (comdlg32), `ChooseColorW`, `ImageList_*`, `InitCommonControlsEx`, `TrackMouseEvent`.
- **GDI**: `BitBlt`, `StretchBlt`, `StretchDIBits`, `CreateCompatibleDC/Bitmap`, `CreateDIBSection`, `SelectObject`, `SetBkColor`, `TextOutW`, `DrawTextW`, `GetTextMetricsW`, etc.
- **Threading**: `CreateThread`, `_beginthreadex`, `CreateEventW`, `ResetEvent`, `SetEvent`, `WaitForSingleObject`, `WaitForMultipleObjects`, `Enter/LeaveCriticalSection`, `InitializeCriticalSection`, `TlsSetValue/TlsFree`, `FlsAlloc/Free`.
- **CLSID/COM objects**: `CLSIDFromString`, `CreateBindCtx`, `CreateStreamOnHGlobal`.

## Strings notáveis
- Named pipe: `\\.\PIPE\Everything Service`
- HTTP server: "Everything HTTP Server"
- ETP/FTP: "Everything ETP/FTP"
- Debug log: "Everything Debug Log.txt"
- Hotkey/menu: "Next Everything Window", "Everything File List", "Everything File List Editor"
- Icon: "load everything icon"
- ES URLs: "Open \"Everything\" from ES: URLs."
- Comandos de mídia: "play EVERYTHING", "stop EVERYTHING"

## Mecanismos de comunicação
- **Named pipe** para o serviço (elevação UAC): cliente GUI fala com o serviço via `\\.\PIPE\Everything Service`.
- **Socket TCP** para HTTP server e ETP/FTP (portas configuráveis).
- **Windows messages** + hotkeys (`RegisterHotKey`) para atalhos globais.
- **Drag & drop** OLE para integração com Explorer.

## Persistência/config
- Registry (`Reg*`) para configurações e associações.
- Arquivos: database de índice ("Everything database."), file lists (`.efu`), debug log.
- `GetTempPathW`, `GetWindowsDirectoryW`, `GetSystemDirectoryW`.

## Pontos de interesse para análise mais profunda
- `sub_1400EC2E0` — abre named pipe do serviço (DATA XREF do pipe string).
- `sub_1400CF020` — "load everything icon".
- `sub_14000B7D0` / `sub_14000B440` — strings "Everything".
- `sub_140001000`, `sub_1400045C0` etc. — entry points / init.
- Serviço: handlers em torno de `RegisterServiceCtrlHandlerW`.

## Observações
- Compilado com MSVC (CRT: `_tmainCRTStartup`, `_heap_init`, `_ioinit`, `_mtinit`, `_setmbcp_nolock`), x64.
- Usa SEH (`__C_specific_handler`, `RtlLookupFunctionEntry`, `RtlVirtualUnwind`, `RtlCaptureContext`) — típico de MSVC x64.
- Sem símbolos (stripped), mas strings e imports dão boa visibilidade.
- Possui recursos (ícones) carregados via `LoadImageW`/`LoadIconW`/`FindResourceW`/`LoadResource`.

## O SEGREDO DA VELOCIDADE (o que você perguntou)

Everything não "procura" arquivos — ele **lê a tabela de arquivos do filesystem diretamente** e depois **monitora mudanças incrementais**. É por isso que é instantâneo.

### 1. Leitura direta da MFT (Master File Table) do NTFS
Em vez de fazer `FindFirstFile`/`FindNextFile` recursivo (lento, I/O de diretório), Everything abre o volume (`\\.\X:`) e usa `DeviceIoControl` com:
- `FSCTL_ENUM_USN_DATA` — enumera registros USN (cada registro = um arquivo/pasta com FRN, parent FRN, nome, atributos, timestamps).
- Leitura direta de MFT records (strings "BAD mft record length", "extended mft ...", "enum mft" confirmam parser próprio de MFT).
- Suporte a **ReFS** também (`refs monitor`, `refs volume`, `FSCTL_GET_REFS_FILE_ID`-style).

Isso lê o disco em blocos sequenciais (uma passada linear) em vez de milhares de seeks aleatórios — ordens de magnitude mais rápido.

### 2. USN Journal (Change Journal) para updates incrementais
NTFS mantém um diário de mudanças (`$UsnJrnl:$J`). Everything:
- Cria/usa o journal (`-create-usn-journal`, `-delete-usn-journal`, `CreateUsnJournal`).
- Monitora via thread `_db_monitor_ntfs_process_usn_records` / `_db_monitor_ntfs_process_usn_records_thread_proc`.
- Quando um arquivo é criado/deletado/renomeado, só processa o delta do journal — não re-indexa nada.
- Strings: "Querying USN Journal", "CheckJournalId", "unprocessed MFT data runs".

### 3. Índice em memória + persistência em DB
- Mantém estrutura de índices em RAM (`_db_index_add`, `Allocating file indexes`, "allocate file indexes, removing orphaned files").
- Persiste em database (`.db`) para cold start rápido (carrega em vez de re-ler MFT).
- Orphan cleanup ao carregar.

### 4. Monitoramento de volume
- `FindFirstChangeNotificationW`/`FindNextChangeNotification` para volumes não-NTFS (FAT/exFAT/removíveis).
- Monitores NTFS/ReFS separados por volume (`ntfs monitors started`, `refs monitors started`).
- Auto-include de volumes fixos/removíveis configurável.

### 5. Concorrência
- Threads dedicadas por volume/monitor (`CreateThread`, `_beginthreadex`), critical sections para o índice compartilhado.
- Threads de processamento USN separadas do thread de UI.

### Resumo em uma frase
**Everything é rápido porque lê a MFT do NTFS em uma passada linear via DeviceIoControl (FSCTL_ENUM_USN_DATA) em vez de caminhar diretórios, e depois mantém o índice atualizado lendo só os deltas do USN Journal ($UsnJrnl) — sem re-scan.**

## Próximos passos sugeridos
1. Mapear funções por XREF das strings de pipe/HTTP/service para entender protocolo.
2. Decompile (F5 no IDA) das funções de serviço (`sub_1400EC2E0`) e indexação (`FindFirstFileW` callers).
3. Analisar formato da database (`.db`) e file lists (`.efu`).
4. Capturar tráfego do HTTP server / ETP para documentar protocolo.
5. Inspecionar recursos (ícones, dialogs) com Resource Hacker.
6. Verificar assinatura digital / certificado do binário.

## Artefatos gerados
- `Everything.exe.i64` — base de dados IDA (28.7 MB)
- `Everything.exe.asm` — disassembly completo (28.7 MB)
- `Everything.exe` original preservado.
