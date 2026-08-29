# MTimer

一个用于每日计划、长期目标与顺计时记录的轻量 WPF 桌面计时器。

## 功能

- 置顶、可拖动的小窗：切换当前任务、开始/停止计时，以及完成当前周期任务。展开面板同样显示当前任务并提供开始/停止。
- 每日计划：按日期维护计划任务与实际计时；每日业务日期在凌晨 4 点切换。新建的每日任务与长期任务默认顺计时，且每项可切换顺/倒计时；双击当天未完成的任务卡片可切换当前计时项。倒计时以 `xhxm` 配合 `±1h`、`±15m`、`±5m` 调整。任务卡的“实计”可在当天或历史日期用 `±15m`、`±5m`、`±1m` 补记或扣除漏记时间，并同步日总时长、统计和倒计时完成状态。日历当天显示实际计时（未开始为 `0m`），未来日期显示计划时长。日历点仅表示当天有实际计时或未跳过的已完成任务，空计划不打点。
- 长期任务：将出现方式（手动/周期）、进度记录（百分比/数量/子项目）与计时方式（顺/倒计时）独立配置；支持 `20%`、`10题`，也可以将“阅读”等父任务拆成“Unity 某项目 35%”这样的持久化子项目。可启用“记录题号”，让每日计划逐条保存任意文本形式的题号，并标记正确、待改或错误；历史日期保持任务结构只读，但允许补录或删除题号，以便修正漏记。
- 长期任务卡片：所有已命名任务均可通过 `+` 加入或恢复当天记录；重复点击不会创建重复任务，周期任务在非设定日加入时仅作为当天例外。子项目分别记录 `0%` 至 `100%` 进度，父任务继续负责加入今日和计时；右下快速卡可查看并微调子项目进度，但不提供删除，删除仅在展开详情中操作。
- 长期进度：右下卡片可用 `− / +` 每次微调 1 个单位。启用题号记录的数量任务完成时，按当天已添加的题号条目数自动累计（正确与错误均计入尝试数）；新增或删除已完成任务的题号会同步修正累计，撤销完成会回退。未启用题号记录时，仍按“每次完成增加”处理。
- 倒计时：可调整默认单次倒计时；加入今日后按该时长倒计时，时间耗尽即自动完成。修改默认时长会同步当天关联任务并保留已计时，例如已完成 2 小时后改为 3 小时会变为剩余 1 小时。
- 顺计时：每日计划不显示倒计时输入，通过 ✓ 标记或撤销完成。
- 周期任务：选择星期后会自动加入符合日期的每日计划；它可以独立搭配顺计时或倒计时，以及百分比或数量进度。
- 展开面板：左侧可在日历、长期任务详情和当月统计之间切换；长期任务备注仅在详情区显示。
- 当月统计：显示实际计时、计划时长、活跃天数、周期任务完成数、每日计时柱状图，以及按日期归集的算法题题号记录和正确/待改/错误/未标记数量。
- PC 同步：本地数据先保存，再通过 Docker Sync API 自动同步；启动、数据修改和 10 秒心跳会触发同步，连续失败五次后暂停五分钟。日历右上角显示产生实际记录的 1–2 字电脑简称，同日来自两台电脑时显示如 `主·本`。

## 本地数据

程序在运行目录维护 `minute_count.txt`、`daily_plans.json`、`long_tasks.json`、本机同步配置 `sync_config.json` 和本机同步状态 `sync_state.json`。长期任务子项目保存在父任务的 `SubTasks` 数组中；每日记录的 `RecordedDeviceIds` 保存参与实际计时、校时、完成或题号记录的设备身份。旧数据缺少这些字段时按空列表读取。旧版长期任务中的 `Progress` 字段会自动迁移为百分比进度；旧周期任务会迁移为“周期 + 顺计时”，旧进度任务会迁移为“手动 + 倒计时”。已有数据无需手动转换。

`sync_state.json` 保存稳定 `DeviceId`、增量 cursor 和待发送 outbox，不应复制到另一台电脑或提交 Git。`minute_count.txt` 仍是当前业务日计时的兼容缓存，不作为独立同步实体。

## PC 同步配置

展开主窗口后点击标题栏中的“同步设置”按钮，可以直接修改电脑简称；保存到 `sync_config.json` 后立即生效，不需要重启。

```json
{
  "deviceName": "主"
}
```

- `deviceName`：电脑简称，只允许 1–2 个 Unicode 文字；修改名称不会改变稳定 `DeviceId`。
- JSON 缺少简称时回退到 `MTIMER_DEVICE_NAME`，再回退到 Windows 电脑名前两个文字。
- 同步服务地址沿用 MWordMemory 的固定部署约定并使用 MTimer 端口：依次检查上次成功地址、Tailscale `http://100.93.235.98:5124`、局域网 `http://192.168.1.88:5124`，不需要每台电脑重复配置。
- `sync_config.json` 与 `sync_state.json` 都是每台电脑独立的本地文件，不参与同步，也不提交 Git。

当前同步协议为 v1，同步粒度为“每日记录按日期”和“整个长期任务列表”。服务端拒绝不兼容的协议版本；stale 写入被拒绝后，客户端会完整拉取当前服务器状态以消除时钟偏差造成的本地假冲突。它适合只在一台电脑上主动计时、在多台 PC 间交替使用；如果未来允许 PC/Android 同时计时，需要把分钟数改为幂等增量或计时事件，不能继续用整个每日记录的最后写入者优先策略。

## Docker Sync API

在仓库根目录启动：

```powershell
docker compose up -d --build
```

服务默认监听 `5124`，提供：

- `GET /health`
- `POST /sync/push`
- `GET /sync/pull?after=<cursor>&protocolVersion=1`

SQLite 数据保存在 Compose 的 `./data/mtimer-sync` 持久卷。NAS 部署可把该卷左侧改为明确的宿主机目录。此轻量服务不提供认证，只应运行在本机或受信任的私网中，不要直接暴露到公网；生产数据库和 `.env` 不应提交仓库。

### 从 Windows 更新现有 QNAP 服务

首次创建 NAS 项目与容器是一次性部署操作，不提供独立初始化脚本。已有 MTimer 容器后，使用 `tools/Deploy-MTimerSyncNas.cmd` 更新指定 Git 提交中的 `MTimer.Sync.Contracts` 与 `MTimer.Sync.Api`：

```powershell
.\tools\Deploy-MTimerSyncNas.cmd NAS_IP NAS_SSH_USER /share/STORAGE_POOL/Container/MTimer HEAD
```

可以把非敏感连接默认值保存到 Windows 用户环境变量，之后双击 CMD 时只需输入 SSH 密码：

```powershell
setx MTIMER_NAS_HOST "NAS_IP"
setx MTIMER_NAS_USER "NAS_SSH_USER"
setx MTIMER_NAS_REMOTE_PATH "/share/STORAGE_POOL/Container/MTimer"
```

脚本只部署 `Revision` 指定的已提交状态，不复制未提交工作区。NAS 项目根目录必须保留 `.dockerignore`，避免 live data 进入 Docker 构建上下文。正式更新前，脚本会检查现有 NAS 项目标记和 NAS-only 源码，准备带时间戳的 `.dockerignore`、compose、旧服务端源码与静止 `sync.db` 备份；随后校验复制后的 SHA-256，重建 `mtimer-sync`，并通过 `/health`、空 `/sync/push` 和 `/sync/pull` 核验实际协议版本。脚本不会删除 NAS-only 源码、live data 或悬空镜像。

正式更新前可先执行只读验证：

```powershell
$env:MTIMER_NAS_VALIDATE_ONLY = "1"
.\tools\Deploy-MTimerSyncNas.cmd NAS_IP NAS_SSH_USER /share/STORAGE_POOL/Container/MTimer HEAD
```

SSH 在停止和重建容器时可能分别请求密码；密码不会保存。需要全自动无人值守更新时，应另外配置 SSH 公钥认证。

## 运行约束

- 普通模式每个 Windows 登录会话只能运行一个 MTimer 实例；重复启动会立即退出，不会读取或修改本地计时数据。
- `--verify-ui` 是隔离的截图验证模式，不受普通实例限制，并且只使用指定的临时数据目录。
- `--verify-ui` 不创建 `sync_state.json`、不启动同步心跳且不访问网络；日期设备简称由固定夹具提供，`SyncSettings` 场景只在临时数据目录读写简称配置夹具并校验固定 endpoint 候选。
