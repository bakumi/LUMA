import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import type { ActionResult, PowerAction, SystemSnapshot } from "./types";

type Tab = "power" | "monitor";
type PowerButton = Exclude<PowerAction, "cancel">;
type IconName =
  | "island"
  | "lock"
  | "moon"
  | "restart"
  | "power"
  | "cpu"
  | "memory"
  | "drive"
  | "network"
  | "download"
  | "upload"
  | "clock"
  | "close"
  | "warning"
  | "check"
  | "arrow";

type Notice = { message: string; scheduledUntil: number | null; isError?: boolean };

const icons: Record<IconName, ReactNode> = {
  island: <><path d="M4 8.5h2v7H4zM9 5h2v14H9zM14 7h2v10h-2zM19 9h2v6h-2z" fill="currentColor" stroke="none" /></>,
  lock: <><rect x="5.5" y="10" width="13" height="10" rx="2.4" /><path d="M8.5 10V7a3.5 3.5 0 0 1 7 0v3M12 14v2.5" /></>,
  moon: <path d="M19.6 15.3A8.3 8.3 0 0 1 8.7 4.4 8.5 8.5 0 1 0 19.6 15.3Z" />,
  restart: <><path d="M20 7v5h-5" /><path d="M19 12a7 7 0 1 1-2.1-5" /></>,
  power: <><path d="M12 2.8v9" /><path d="M6.2 5.9a8 8 0 1 0 11.6 0" /></>,
  cpu: <><rect x="6" y="6" width="12" height="12" rx="2.5" /><path d="M9 9h6v6H9zM9 2.8v3M15 2.8v3M9 18.2v3M15 18.2v3M2.8 9h3M2.8 15h3M18.2 9h3M18.2 15h3" /></>,
  memory: <><rect x="3.5" y="6.5" width="17" height="11" rx="2" /><path d="M7 10h2v4H7zM12 10h2v4h-2zM17 10h.01M6 17.5v2M10 17.5v2M14 17.5v2M18 17.5v2" /></>,
  drive: <><rect x="3.5" y="5" width="17" height="14" rx="3" /><path d="M7.5 14.5h.01M11 14.5h.01M7 9h10" /></>,
  network: <><path d="M4 9.5a12.5 12.5 0 0 1 16 0M7 12.5a8 8 0 0 1 10 0M10 15.5a3.4 3.4 0 0 1 4 0M12 19h.01" /></>,
  download: <><path d="M12 3v11M8 10l4 4 4-4" /><path d="M5 17v3h14v-3" /></>,
  upload: <><path d="M12 14V3M8 7l4-4 4 4" /><path d="M5 17v3h14v-3" /></>,
  clock: <><circle cx="12" cy="12" r="8.5" /><path d="M12 7v5l3.3 2" /></>,
  close: <><path d="m6 6 12 12M18 6 6 18" /></>,
  warning: <><path d="m10.3 4.3-7 12.2A2 2 0 0 0 5 19.5h14a2 2 0 0 0 1.7-3L13.7 4.3a2 2 0 0 0-3.4 0Z" /><path d="M12 9v4M12 16.5h.01" /></>,
  check: <path d="m5 12 4.5 4.5L19 7" />,
  arrow: <><path d="M5 12h14M13 6l6 6-6 6" /></>,
};

function Icon({ name, size = 20, className = "" }: { name: IconName; size?: number; className?: string }) {
  return (
    <svg
      aria-hidden="true"
      className={className}
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      {icons[name]}
    </svg>
  );
}

const powerButtons: { id: PowerButton; title: string; subtitle: string; icon: IconName; tone: string }[] = [
  { id: "lock", title: "Блокировка", subtitle: "Сеанс Windows", icon: "lock", tone: "ice" },
  { id: "sleep", title: "Сон", subtitle: "Быстро вернуться", icon: "moon", tone: "violet" },
  { id: "restart", title: "Перезапуск", subtitle: "Через 15 секунд", icon: "restart", tone: "amber" },
  { id: "shutdown", title: "Выключение", subtitle: "Через 15 секунд", icon: "power", tone: "rose" },
];

const actionCopy: Record<PowerButton, { title: string; description: string; confirm: string }> = {
  lock: { title: "Заблокировать компьютер?", description: "Открытые приложения останутся работать, а для возврата потребуется войти в Windows.", confirm: "Заблокировать" },
  sleep: { title: "Перевести компьютер в сон?", description: "Сеанс сохранится. Нажмите кнопку питания или клавишу, чтобы продолжить работу.", confirm: "Перейти в сон" },
  restart: { title: "Перезапустить компьютер?", description: "Windows начнёт перезапуск через 15 секунд. Его можно отменить из островка.", confirm: "Перезапустить" },
  shutdown: { title: "Выключить компьютер?", description: "Windows начнёт завершение работы через 15 секунд. Его можно отменить из островка.", confirm: "Выключить" },
};

function percent(value: number): number {
  return Math.min(100, Math.max(0, Math.round(value)));
}

function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return "0 Б";
  const units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
  const tier = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  const amount = value / Math.pow(1024, tier);
  return amount.toLocaleString("ru-RU", { maximumFractionDigits: tier > 1 ? 1 : 0 }) + " " + units[tier];
}

function formatRate(value: number): string {
  if (value >= 1024 * 1024) return (value / (1024 * 1024)).toLocaleString("ru-RU", { maximumFractionDigits: 1 }) + " МБ/с";
  if (value >= 1024) return (value / 1024).toLocaleString("ru-RU", { maximumFractionDigits: 0 }) + " КБ/с";
  return Math.round(Math.max(value, 0)) + " Б/с";
}

function formatUptime(seconds: number): string {
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (days > 0) return days + " д " + hours + " ч";
  if (hours > 0) return hours + " ч " + minutes + " мин";
  return minutes + " мин";
}

function Meter({ value, icon, color }: { value: number; icon: IconName; color: string }) {
  const safeValue = percent(value);
  return (
    <div
      className="meter-ring"
      style={{ background: "conic-gradient(" + color + " " + safeValue * 3.6 + "deg, rgba(255,255,255,.075) 0deg)" }}
      aria-label={safeValue + "%"}
    >
      <div className="meter-ring-core"><Icon name={icon} size={18} /></div>
    </div>
  );
}

function NetworkLine({ samples }: { samples: number[] }) {
  const points = useMemo(() => {
    const safe = samples.length ? samples : [0, 0];
    const max = Math.max(...safe, 1);
    return safe.map((value, index) => {
      const x = 2 + (index / Math.max(safe.length - 1, 1)) * 316;
      const y = 48 - (Math.min(value, max) / max) * 36;
      return x.toFixed(1) + "," + y.toFixed(1);
    }).join(" ");
  }, [samples]);

  return (
    <svg className="network-line" viewBox="0 0 320 54" preserveAspectRatio="none" aria-hidden="true">
      <path className="network-guide" d="M0 13.5H320M0 31H320M0 48.5H320" />
      <polyline className="network-line-fill" points={points + " 318,52 2,52"} />
      <polyline className="network-line-stroke" points={points} />
    </svg>
  );
}

function App() {
  const [expanded, setExpanded] = useState(false);
  const [tab, setTab] = useState<Tab>("power");
  const [snapshot, setSnapshot] = useState<SystemSnapshot | null>(null);
  const [sparkline, setSparkline] = useState<number[]>(() => Array.from({ length: 38 }, () => 0.2));
  const [dialog, setDialog] = useState<PowerButton | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [remaining, setRemaining] = useState(0);
  const [busy, setBusy] = useState(false);
  const [clock, setClock] = useState(() => new Date());
  const expandedRef = useRef(expanded);
  expandedRef.current = expanded;

  const setExpandedNative = useCallback(async (next: boolean) => {
    if (next) {
      try {
        await invoke("set_expanded", { expanded: true });
      } catch {
        // The development preview can be opened without the native Tauri host.
      }
      setExpanded(true);
      expandedRef.current = true;
      return;
    }

    setExpanded(false);
    expandedRef.current = false;
    window.setTimeout(() => {
      void invoke("set_expanded", { expanded: false }).catch(() => undefined);
    }, 260);
  }, []);

  const toggleIsland = useCallback(() => {
    void setExpandedNative(!expandedRef.current);
  }, [setExpandedNative]);

  useEffect(() => {
    let mounted = true;
    void setExpandedNative(true);

    const refresh = async () => {
      try {
        const next = await invoke<SystemSnapshot>("get_system_metrics");
        if (!mounted) return;
        setSnapshot(next);
        const networkActivity = next.network.downBytesPerSecond + next.network.upBytesPerSecond;
        setSparkline((current) => [...current.slice(-37), Math.max(0.2, networkActivity)]);
      } catch {
        // Metrics are supplied by the native sidecar in the packaged app.
      }
    };

    void refresh();
    const interval = window.setInterval(() => void refresh(), 1600);
    const clockInterval = window.setInterval(() => setClock(new Date()), 1000);
    let unlisten: (() => void) | undefined;
    void listen("toggle-island", () => toggleIsland()).then((stop) => {
      if (mounted) unlisten = stop;
      else stop();
    }).catch(() => undefined);

    return () => {
      mounted = false;
      window.clearInterval(interval);
      window.clearInterval(clockInterval);
      unlisten?.();
    };
  }, [setExpandedNative, toggleIsland]);

  useEffect(() => {
    if (!notice?.scheduledUntil) {
      setRemaining(0);
      return;
    }
    const update = () => setRemaining(Math.max(0, Math.ceil((notice.scheduledUntil! - Date.now()) / 1000)));
    update();
    const interval = window.setInterval(update, 250);
    return () => window.clearInterval(interval);
  }, [notice]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        if (dialog) setDialog(null);
        else if (expandedRef.current) void setExpandedNative(false);
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [dialog, setExpandedNative]);

  const runAction = async () => {
    if (!dialog || busy) return;
    setBusy(true);
    try {
      const result = await invoke<ActionResult>("perform_power_action", { action: dialog });
      const scheduledUntil = result.scheduled ? Date.now() + 15000 : null;
      setNotice({ message: result.message, scheduledUntil });
      setDialog(null);
    } catch (error) {
      setNotice({ message: String(error), scheduledUntil: null, isError: true });
    } finally {
      setBusy(false);
    }
  };

  const cancelScheduledAction = async () => {
    setBusy(true);
    try {
      const result = await invoke<ActionResult>("perform_power_action", { action: "cancel" });
      setNotice({ message: result.message, scheduledUntil: null });
    } catch (error) {
      setNotice({ message: String(error), scheduledUntil: null, isError: true });
    } finally {
      setBusy(false);
    }
  };

  const timeText = clock.toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" });
  const dateText = clock.toLocaleDateString("ru-RU", { weekday: "long", day: "numeric", month: "long" });

  return (
    <main className="overlay-stage">
      <section className={"island " + (expanded ? "island-expanded" : "island-collapsed")} aria-label="WinIsland">
        <button
          className="island-pill"
          onClick={toggleIsland}
          aria-expanded={expanded}
          aria-controls="island-panel"
          title={expanded ? "Свернуть островок" : "Открыть островок"}
        >
          <span className="pill-mark"><Icon name="island" size={17} /></span>
          {expanded ? (
            <>
              <span className="pill-wordmark">WINISLAND</span>
              <span className="pill-separator" />
              <span className="pill-clock">{timeText}</span>
              <span className="pill-wave" aria-hidden="true"><i /><i /><i /><i /><i /><i /><i /></span>
            </>
          ) : (
            <>
              <span className="pill-compact-label">WinIsland</span>
              <span className="pill-wave" aria-hidden="true"><i /><i /><i /><i /><i /><i /><i /></span>
            </>
          )}
          <span className="pill-live" aria-label="Работает" />
        </button>

        <div id="island-panel" className="island-panel" aria-hidden={!expanded}>
          <header className="panel-header">
            <div className="panel-title-group">
              <span className="panel-kicker">WINDOWS COMPANION</span>
              <span className="panel-title">Всё под рукой<span className="title-period">.</span></span>
              <span className="panel-date">{dateText}</span>
            </div>
            <nav className="panel-tabs" aria-label="Вкладки островка">
              <button className={"tab-button " + (tab === "power" ? "tab-active" : "")} onClick={() => setTab("power")}>
                <Icon name="power" size={14} />Питание
              </button>
              <button className={"tab-button " + (tab === "monitor" ? "tab-active" : "")} onClick={() => setTab("monitor")}>
                <Icon name="network" size={15} />Монитор
              </button>
            </nav>
            <button className="collapse-button" onClick={() => void setExpandedNative(false)} aria-label="Свернуть" title="Свернуть">
              <Icon name="close" size={17} />
            </button>
          </header>

          <div className="panel-body">
            {tab === "power" ? (
              <section className="power-view" aria-label="Управление питанием">
                <div className="section-heading">
                  <div>
                    <p className="eyebrow">БЫСТРЫЕ ДЕЙСТВИЯ</p>
                    <h1>Управление системой</h1>
                  </div>
                  <span className="safe-note"><span className="safe-dot" />Подтверждение перед действием</span>
                </div>

                <div className="power-grid">
                  {powerButtons.map((item, index) => (
                    <button className="power-action" key={item.id} onClick={() => setDialog(item.id)} style={{ animationDelay: index * 45 + "ms" }}>
                      <span className={"action-orb tone-" + item.tone}>
                        <span className="orb-halo" />
                        <Icon name={item.icon} size={25} />
                      </span>
                      <span className="action-title">{item.title}</span>
                      <span className="action-subtitle">{item.subtitle}</span>
                    </button>
                  ))}
                </div>

                <div className="power-footer">
                  <div className="host-mark"><span className="host-icon"><Icon name="cpu" size={15} /></span>
                    <span><b>{snapshot?.hostName || "Мой компьютер"}</b><small>{snapshot?.osVersion || "Windows · готов к работе"}</small></span>
                  </div>
                  <div className="uptime-chip"><Icon name="clock" size={15} /><span>В сети <b>{snapshot ? formatUptime(snapshot.uptimeSeconds) : "—"}</b></span></div>
                </div>
              </section>
            ) : (
              <section className="monitor-view" aria-label="Состояние компьютера">
                <div className="section-heading monitor-heading">
                  <div>
                    <p className="eyebrow">СОСТОЯНИЕ В РЕАЛЬНОМ ВРЕМЕНИ</p>
                    <h1>Система и сеть</h1>
                  </div>
                  <span className="live-chip"><i />LIVE <span>{snapshot ? "обновлено сейчас" : "подключение"}</span></span>
                </div>

                <div className="metrics-grid">
                  <article className="metric-card cpu-card">
                    <div className="metric-card-top"><span className="metric-symbol"><Icon name="cpu" size={16} /></span><span className="metric-label">ПРОЦЕССОР</span><Meter value={snapshot?.cpu.usage || 0} icon="cpu" color="#8dd5ff" /></div>
                    <div className="metric-number">{snapshot ? percent(snapshot.cpu.usage) : "—"}<span>%</span></div>
                    <div className="metric-caption" title={snapshot?.cpu.name}>{snapshot?.cpu.name || "Считываем оборудование"}</div>
                    <div className="metric-foot"><span>{snapshot?.cpu.cores ?? "—"} ядер</span><span>{snapshot?.cpu.threads ?? "—"} потоков</span></div>
                  </article>

                  <article className="metric-card memory-card">
                    <div className="metric-card-top"><span className="metric-symbol"><Icon name="memory" size={16} /></span><span className="metric-label">ПАМЯТЬ</span><Meter value={snapshot?.memory.usage || 0} icon="memory" color="#c8adff" /></div>
                    <div className="metric-number">{snapshot ? percent(snapshot.memory.usage) : "—"}<span>%</span></div>
                    <div className="metric-caption">{snapshot ? formatBytes(snapshot.memory.usedBytes) + " из " + formatBytes(snapshot.memory.totalBytes) : "Считываем память"}</div>
                    <div className="metric-foot"><span>{snapshot ? formatBytes(snapshot.memory.availableBytes) + " доступно" : "—"}</span><span>RAM</span></div>
                  </article>

                  <article className="metric-card disk-card">
                    <div className="metric-card-top"><span className="metric-symbol"><Icon name="drive" size={16} /></span><span className="metric-label">НАКОПИТЕЛЬ</span><Meter value={snapshot?.disk?.usage || 0} icon="drive" color="#ffc584" /></div>
                    <div className="metric-number">{snapshot?.disk ? percent(snapshot.disk.usage) : "—"}<span>%</span></div>
                    <div className="metric-caption">{snapshot?.disk ? snapshot.disk.name + " · " + formatBytes(snapshot.disk.totalBytes) : "Основной диск"}</div>
                    <div className="metric-foot"><span>{snapshot?.disk ? formatBytes(snapshot.disk.availableBytes) + " свободно" : "—"}</span><span>SSD / HDD</span></div>
                  </article>

                  <article className="network-card">
                    <div className="network-card-head">
                      <div className="network-name-wrap">
                        <span className="metric-symbol network-symbol"><Icon name="network" size={17} /></span>
                        <span><b>Сетевой трафик</b><small>{snapshot?.network.interfaceName || "Ожидание сети"}</small></span>
                      </div>
                      <span className={"connection-state " + (snapshot?.network.connected ? "connection-on" : "")}>{snapshot?.network.connected ? "Подключено" : "Не в сети"}</span>
                    </div>
                    <div className="network-chart"><NetworkLine samples={sparkline} /></div>
                    <div className="network-card-foot">
                      <span className="traffic-stat"><i className="traffic-dot traffic-down" /><Icon name="download" size={14} /><b>{formatRate(snapshot?.network.downBytesPerSecond || 0)}</b><small>загрузка</small></span>
                      <span className="traffic-stat"><i className="traffic-dot traffic-up" /><Icon name="upload" size={14} /><b>{formatRate(snapshot?.network.upBytesPerSecond || 0)}</b><small>отдача</small></span>
                      <span className="network-total">{snapshot?.network.activeInterfaces ?? 0} активных адаптера</span>
                    </div>
                  </article>
                </div>

                <div className="monitor-footer">
                  <span className="monitor-device"><Icon name="cpu" size={13} />{snapshot?.osVersion || "Windows"}</span>
                  <span className="monitor-uptime"><Icon name="clock" size={13} />Работает {snapshot ? formatUptime(snapshot.uptimeSeconds) : "—"}</span>
                  <span className="refresh-indicator"><i />Обновление каждые 1,6 с</span>
                </div>
              </section>
            )}
          </div>

          <footer className="panel-footnote">
            <span>WinIsland<span className="footnote-divider">/</span>ТВОЯ WINDOWS, ЧУТЬ БЛИЖЕ</span>
            <span className="shortcut-hint"><kbd>Ctrl</kbd><kbd>Shift</kbd><kbd>Space</kbd><span>открыть</span></span>
          </footer>
        </div>
      </section>

      {notice && (
        <aside className={"action-notice " + (notice.isError ? "notice-error" : "")} role="status">
          <span className="notice-icon"><Icon name={notice.isError ? "warning" : "check"} size={17} /></span>
          <span className="notice-message">{notice.message}{notice.scheduledUntil ? <small>Система начнёт действие через {remaining} с</small> : null}</span>
          {notice.scheduledUntil ? <button className="notice-cancel" onClick={() => void cancelScheduledAction()} disabled={busy}>Отменить</button> : <button className="notice-close" onClick={() => setNotice(null)} aria-label="Закрыть уведомление"><Icon name="close" size={16} /></button>}
        </aside>
      )}

      {dialog && (
        <div className="dialog-scrim" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setDialog(null); }}>
          <section className="confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="dialog-title">
            <span className={"dialog-icon dialog-" + dialog}><Icon name={powerButtons.find((item) => item.id === dialog)!.icon} size={22} /></span>
            <p className="eyebrow">ДЕЙСТВИЕ WINDOWS</p>
            <h2 id="dialog-title">{actionCopy[dialog].title}</h2>
            <p className="dialog-description">{actionCopy[dialog].description}</p>
            <div className="dialog-actions">
              <button className="dialog-back" onClick={() => setDialog(null)} disabled={busy}>Остаться</button>
              <button className={"dialog-confirm confirm-" + dialog} onClick={() => void runAction()} disabled={busy}>
                {busy ? <span className="button-spinner" /> : null}{actionCopy[dialog].confirm}
              </button>
            </div>
            <span className="dialog-note"><Icon name="warning" size={13} />Для перезапуска и выключения доступна отмена в течение 15 секунд</span>
          </section>
        </div>
      )}
    </main>
  );
}

export default App;
