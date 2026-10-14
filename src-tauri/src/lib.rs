use std::env;
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::os::windows::process::CommandExt;
use std::sync::Mutex;
use std::thread;
use std::time::Instant;

use serde::Serialize;
use sysinfo::{Disks, InterfaceOperationalState, Networks, System};
use tauri::Emitter;
use tauri::Manager;
use tauri::PhysicalPosition;
use tauri::PhysicalSize;
use tauri::AppHandle;
use tauri::State;
use tauri_plugin_global_shortcut::{Builder as ShortcutBuilder, GlobalShortcutExt, ShortcutState};
use windows_sys::Win32::System::Power::SetSuspendState;
use windows_sys::Win32::System::Shutdown::LockWorkStation;

const COLLAPSED_WIDTH: f64 = 164.0;
const COLLAPSED_HEIGHT: f64 = 48.0;
const EXPANDED_WIDTH: f64 = 760.0;
const EXPANDED_HEIGHT: f64 = 520.0;
const SHUTDOWN_DELAY_SECONDS: &str = "15";
const CREATE_NO_WINDOW: u32 = 0x08000000;

struct NetworkTracker {
    adapters: Networks,
    last_refresh: Instant,
}

struct MonitorState {
    system: Mutex<System>,
    disks: Mutex<Disks>,
    network: Mutex<NetworkTracker>,
}

impl MonitorState {
    fn new() -> Self {
        let mut system = System::new_all();
        thread::sleep(sysinfo::MINIMUM_CPU_UPDATE_INTERVAL);
        system.refresh_cpu_usage();

        Self {
            system: Mutex::new(system),
            disks: Mutex::new(Disks::new_with_refreshed_list()),
            network: Mutex::new(NetworkTracker {
                adapters: Networks::new_with_refreshed_list(),
                last_refresh: Instant::now(),
            }),
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct CpuStats {
    name: String,
    usage: f32,
    cores: usize,
    threads: usize,
    frequency_mhz: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct MemoryStats {
    total_bytes: u64,
    used_bytes: u64,
    available_bytes: u64,
    usage: f32,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct DiskStats {
    name: String,
    mount_point: String,
    total_bytes: u64,
    available_bytes: u64,
    usage: f32,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct NetworkStats {
    connected: bool,
    interface_name: String,
    down_bytes_per_second: f64,
    up_bytes_per_second: f64,
    received_total_bytes: u64,
    transmitted_total_bytes: u64,
    active_interfaces: usize,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SystemSnapshot {
    host_name: String,
    os_version: String,
    uptime_seconds: u64,
    cpu: CpuStats,
    memory: MemoryStats,
    disk: Option<DiskStats>,
    network: NetworkStats,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ActionResult {
    message: String,
    scheduled: bool,
}

#[tauri::command]
fn get_system_metrics(state: State<'_, MonitorState>) -> Result<SystemSnapshot, String> {
    let mut system = state.system.lock().map_err(|_| "Не удалось прочитать данные процессора".to_owned())?;
    system.refresh_cpu_usage();
    system.refresh_memory();

    let cpus = system.cpus();
    let cpu_name = cpus
        .first()
        .map(|cpu| cpu.brand().trim().to_owned())
        .filter(|name| !name.is_empty())
        .unwrap_or_else(|| "Процессор Windows".to_owned());
    let cpu_frequency = if cpus.is_empty() {
        0
    } else {
        cpus.iter().map(|cpu| cpu.frequency()).sum::<u64>() / cpus.len() as u64
    };
    let physical_cores = System::physical_core_count().unwrap_or(cpus.len());
    let cpu = CpuStats {
        name: cpu_name,
        usage: system.global_cpu_usage().clamp(0.0, 100.0),
        cores: physical_cores,
        threads: cpus.len(),
        frequency_mhz: cpu_frequency,
    };

    let total_memory = system.total_memory();
    let used_memory = system.used_memory().min(total_memory);
    let memory = MemoryStats {
        total_bytes: total_memory,
        used_bytes: used_memory,
        available_bytes: system.available_memory(),
        usage: if total_memory == 0 { 0.0 } else { used_memory as f32 / total_memory as f32 * 100.0 },
    };

    let mut disks = state.disks.lock().map_err(|_| "Не удалось прочитать данные накопителя".to_owned())?;
    disks.refresh(false);
    let system_drive = env::var("SystemDrive").unwrap_or_else(|_| "C:".to_owned());
    let expected_mount = format!("{}\\", system_drive.trim_end_matches(['\\', '/']));
    let disk = disks
        .list()
        .iter()
        .find(|disk| disk.mount_point().to_string_lossy().eq_ignore_ascii_case(&expected_mount))
        .or_else(|| disks.list().first())
        .map(|disk| {
            let total = disk.total_space();
            let available = disk.available_space();
            DiskStats {
                name: disk.name().to_string_lossy().into_owned(),
                mount_point: disk.mount_point().to_string_lossy().into_owned(),
                total_bytes: total,
                available_bytes: available,
                usage: if total == 0 { 0.0 } else { (total.saturating_sub(available)) as f32 / total as f32 * 100.0 },
            }
        });

    let mut tracker = state.network.lock().map_err(|_| "Не удалось прочитать данные сети".to_owned())?;
    tracker.adapters.refresh(true);
    let elapsed = tracker.last_refresh.elapsed().as_secs_f64().max(0.1);
    tracker.last_refresh = Instant::now();
    let active = tracker
        .adapters
        .iter()
        .filter(|(name, data)| {
            !name.to_lowercase().contains("loopback")
                && data.operational_state() == InterfaceOperationalState::Up
        })
        .collect::<Vec<_>>();
    let down_bytes = active.iter().map(|(_, data)| data.received()).sum::<u64>();
    let up_bytes = active.iter().map(|(_, data)| data.transmitted()).sum::<u64>();
    let received_total = active.iter().map(|(_, data)| data.total_received()).sum::<u64>();
    let transmitted_total = active.iter().map(|(_, data)| data.total_transmitted()).sum::<u64>();
    let interface_name = active
        .first()
        .map(|(name, _)| name.to_string())
        .unwrap_or_else(|| "Сетевой адаптер не подключён".to_owned());
    let network = NetworkStats {
        connected: !active.is_empty(),
        interface_name,
        down_bytes_per_second: down_bytes as f64 / elapsed,
        up_bytes_per_second: up_bytes as f64 / elapsed,
        received_total_bytes: received_total,
        transmitted_total_bytes: transmitted_total,
        active_interfaces: active.len(),
    };

    Ok(SystemSnapshot {
        host_name: System::host_name().unwrap_or_else(|| "Мой компьютер".to_owned()),
        os_version: System::long_os_version().unwrap_or_else(|| "Windows".to_owned()),
        uptime_seconds: System::uptime(),
        cpu,
        memory,
        disk,
        network,
    })
}

#[tauri::command]
fn perform_power_action(action: String) -> Result<ActionResult, String> {
    match action.as_str() {
        "lock" => {
            let succeeded = unsafe { LockWorkStation() } != 0;
            if !succeeded {
                return Err("Windows не смогла заблокировать сеанс".to_owned());
            }
            Ok(ActionResult { message: "Сеанс Windows заблокирован".to_owned(), scheduled: false })
        }
        "sleep" => {
            let succeeded = unsafe { SetSuspendState(false, false, false) };
            if !succeeded {
                return Err("Windows не смогла перевести компьютер в спящий режим".to_owned());
            }
            Ok(ActionResult { message: "Компьютер переходит в спящий режим".to_owned(), scheduled: false })
        }
        "restart" => {
            run_shutdown_command(&["/r", "/t", SHUTDOWN_DELAY_SECONDS, "/c", "Restart scheduled by WinIsland"])?;
            Ok(ActionResult { message: "Перезапуск запланирован".to_owned(), scheduled: true })
        }
        "shutdown" => {
            run_shutdown_command(&["/s", "/t", SHUTDOWN_DELAY_SECONDS, "/c", "Shutdown scheduled by WinIsland"])?;
            Ok(ActionResult { message: "Выключение запланировано".to_owned(), scheduled: true })
        }
        "cancel" => {
            run_shutdown_command(&["/a"])?;
            Ok(ActionResult { message: "Запланированное действие отменено".to_owned(), scheduled: false })
        }
        _ => Err("Неизвестная команда питания".to_owned()),
    }
}

fn run_shutdown_command(arguments: &[&str]) -> Result<(), String> {
    let system_root = env::var_os("SystemRoot").unwrap_or_else(|| "C:\\Windows".into());
    let shutdown_exe: PathBuf = PathBuf::from(system_root).join("System32").join("shutdown.exe");
    Command::new(shutdown_exe)
        .args(arguments)
        .creation_flags(CREATE_NO_WINDOW)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map(|_| ())
        .map_err(|error| format!("Не удалось запустить команду Windows: {error}"))
}

#[tauri::command]
fn set_expanded(app: AppHandle, expanded: bool) -> Result<(), String> {
    let window = app
        .get_webview_window("main")
        .ok_or_else(|| "Окно WinIsland не найдено".to_owned())?;
    let monitor = window
        .current_monitor()
        .map_err(|error| error.to_string())?
        .or(window.primary_monitor().map_err(|error| error.to_string())?);

    let (scale, monitor_x, monitor_y, monitor_width, monitor_height) = match monitor {
        Some(monitor) => {
            let position = monitor.position();
            let size = monitor.size();
            (monitor.scale_factor(), position.x, position.y, size.width, size.height)
        }
        None => (1.0, 0, 0, 1920, 1080),
    };

    let available_width = monitor_width as f64 / scale;
    let available_height = monitor_height as f64 / scale;
    let (logical_width, logical_height) = if expanded {
        (EXPANDED_WIDTH.min(available_width - 16.0).max(420.0), EXPANDED_HEIGHT.min(available_height - 8.0).max(360.0))
    } else {
        (COLLAPSED_WIDTH, COLLAPSED_HEIGHT)
    };
    let width = (logical_width * scale).round().max(1.0) as u32;
    let height = (logical_height * scale).round().max(1.0) as u32;
    let x = monitor_x + (monitor_width as i32 - width as i32) / 2;

    window.set_size(PhysicalSize::new(width, height)).map_err(|error| error.to_string())?;
    window.set_position(PhysicalPosition::new(x, monitor_y)).map_err(|error| error.to_string())?;
    window.show().map_err(|error| error.to_string())?;
    if expanded {
        window.set_focus().map_err(|error| error.to_string())?;
    }
    Ok(())
}

pub fn run() {
    tauri::Builder::default()
        .manage(MonitorState::new())
        .plugin(
            ShortcutBuilder::new()
                .with_handler(|app, _shortcut, event| {
                    if event.state == ShortcutState::Pressed {
                        if let Some(window) = app.get_webview_window("main") {
                            let _ = window.show();
                            let _ = window.set_focus();
                        }
                        let _ = app.emit_to("main", "toggle-island", ());
                    }
                })
                .build(),
        )
        .setup(|app| {
            app.global_shortcut().register("Ctrl+Shift+Space")?;
            if let Some(window) = app.get_webview_window("main") {
                set_expanded(app.handle().clone(), false).map_err(std::io::Error::other)?;
                window.show()?;
            }
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![get_system_metrics, perform_power_action, set_expanded])
        .run(tauri::generate_context!())
        .expect("WinIsland failed to start");
}
