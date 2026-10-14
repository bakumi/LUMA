export type PowerAction = "lock" | "sleep" | "restart" | "shutdown" | "cancel";

export interface ActionResult {
  message: string;
  scheduled: boolean;
}

export interface CpuStats {
  name: string;
  usage: number;
  cores: number;
  threads: number;
  frequencyMhz: number;
}

export interface MemoryStats {
  totalBytes: number;
  usedBytes: number;
  availableBytes: number;
  usage: number;
}

export interface DiskStats {
  name: string;
  mountPoint: string;
  totalBytes: number;
  availableBytes: number;
  usage: number;
}

export interface NetworkStats {
  connected: boolean;
  interfaceName: string;
  downBytesPerSecond: number;
  upBytesPerSecond: number;
  receivedTotalBytes: number;
  transmittedTotalBytes: number;
  activeInterfaces: number;
}

export interface SystemSnapshot {
  hostName: string;
  osVersion: string;
  uptimeSeconds: number;
  cpu: CpuStats;
  memory: MemoryStats;
  disk: DiskStats | null;
  network: NetworkStats;
}
