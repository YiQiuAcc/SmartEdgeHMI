/**
 * SmartEdgeHMI 虚拟下位机: 在同一份仿真状态上同时实现 JSON 行协议与 Modbus RTU 从站
 *
 * 启动方式:
 *   pnpm run sim:json    JSON 主动上报模式, 1 秒一帧
 *   pnpm run sim:modbus  Modbus RTU 从站模式, 由上位机轮询驱动
 *   pnpm run sim:fault   JSON 模式叠加定时故障窗口, 用于报警链路端到端验证
 *
 * 与上位机的契约: 寄存器映射与错误码含义必须和 Firmware/src/main.cpp 保持一致,
 * 改动任一侧都要同步 SmartEdgeHMI 的 Common/AppConstants.cs 与 Enums.cs
 */

import { SerialPort } from "serialport";
import { ReadlineParser } from "@serialport/parser-readline";

// 仿真状态: 温湿度随机游走, status 随阈值判定
const state = {
  temperature: 25.0,
  humidity: 45.0,
  status: 1, // 1 正常, 4 故障(DeviceStatus.Fault)
  threshold: 80.0, // 报警阈值, 单位 °C, 可通过 FC06 写入
};

// 模拟温湿度随时间自然波动 (Random Walk)
setInterval(() => {
  state.temperature += (Math.random() - 0.5) * 0.5;
  state.humidity += (Math.random() - 0.5) * 1.2;

  // 限制在合理物理范围内
  state.temperature = Math.max(10, Math.min(60, state.temperature));
  state.humidity = Math.max(20, Math.min(95, state.humidity));

  // 阈值监控 (Modbus 与 JSON 共用)
  if (state.temperature > state.threshold) {
    state.status = 4; // DeviceStatus.Fault
  } else {
    state.status = 1; // DeviceStatus.Online
  }
}, 1000);

// 故障注入: 按时间窗口确定地进入/退出故障态, 用于在不改代码的前提下复现报警链路
// 计时基准是进程启动时刻, 用 Date.now() - startTime 判定, 不依赖 setInterval 的累计次数
const startTime = Date.now();
const faultConfig = { enabled: false, startMs: 0, durationMs: 0, code: 302 };
let faultActive = false; // 故障期变量: JSON 与 Modbus 两条路径共用

// 纯函数: elapsedMs 是否落在 [startMs, startMs + durationMs) 故障窗口内
function isFaultActive(elapsedMs, startMs, durationMs) {
  return elapsedMs >= startMs && elapsedMs < startMs + durationMs;
}

// 刷新故障期变量, 进入/退出各打一行 [FAULT] 日志 (JSON 与 Modbus 共用同一时间判断)
function refreshFaultState() {
  if (!faultConfig.enabled) return false;
  const elapsedMs = Date.now() - startTime;
  const active = isFaultActive(
    elapsedMs,
    faultConfig.startMs,
    faultConfig.durationMs,
  );
  if (active !== faultActive) {
    faultActive = active;
    const tSec = Math.floor(elapsedMs / 1000);
    console.log(
      active
        ? `[FAULT] 进入故障态 code=${faultConfig.code} (t=${tSec}s)`
        : `[FAULT] 退出故障态 (t=${tSec}s)`,
    );
  }
  return faultActive;
}

// 核心入口与配置
const mode = (process.argv[2] || "json").toLowerCase();
const portName = process.argv[3] || "COM2";
const baudRate = parseInt(process.argv[4] || "115200", 10);

// 故障注入参数: [--fault-window <开始秒> <持续秒>] [--fault-code <错误码>]
const FAULT_USAGE =
  "用法: node virtual-device.js <mode> <port> <baud> [--fault-window <开始秒> <持续秒>] [--fault-code <错误码>]";

let faultWindowSec = null; // { startSec, durationSec }
let faultCode = 302; // ErrorCode.ThresholdExceeded

const flagArgs = process.argv.slice(5);
for (let i = 0; i < flagArgs.length; i++) {
  if (flagArgs[i] === "--fault-window") {
    const startSec = Number(flagArgs[i + 1]);
    const durationSec = Number(flagArgs[i + 2]);
    if (
      flagArgs[i + 1] === undefined ||
      flagArgs[i + 2] === undefined ||
      !Number.isFinite(startSec) ||
      !Number.isFinite(durationSec) ||
      startSec < 0 ||
      durationSec <= 0
    ) {
      console.log(FAULT_USAGE);
      process.exit(1);
    }
    faultWindowSec = { startSec, durationSec };
    i += 2;
  } else if (flagArgs[i] === "--fault-code") {
    const code = Number(flagArgs[i + 1]);
    if (
      flagArgs[i + 1] === undefined ||
      !Number.isInteger(code) ||
      code < 0 ||
      code > 65535
    ) {
      console.log(FAULT_USAGE);
      process.exit(1);
    }
    faultCode = code;
    i += 1;
  } else {
    // 未知参数不静默忽略
    console.log(FAULT_USAGE);
    process.exit(1);
  }
}

if (faultWindowSec) {
  faultConfig.enabled = true;
  faultConfig.startMs = faultWindowSec.startSec * 1000;
  faultConfig.durationMs = faultWindowSec.durationSec * 1000;
  faultConfig.code = faultCode;
}

console.log(`\n启动虚拟设备模拟器...`);
console.log(`端口: ${portName} @ ${baudRate} bps`);
console.log(
  `模式: ${mode === "modbus" ? "Modbus RTU (被动轮询)" : "JSON-Lines (主动上报)"}\n`,
);

if (faultConfig.enabled) {
  console.log(
    `[FAULT] 计划: 第 ${faultWindowSec.startSec} 秒进入故障态, 持续 ${faultWindowSec.durationSec} 秒, code=${faultConfig.code}`,
  );
  // 故障窗口巡检: 串口无流量 (如 Modbus 无人轮询) 时也能准点打印进入/退出日志
  setInterval(refreshFaultState, 200);
}

const port = new SerialPort({ path: portName, baudRate });

port.on("open", () => {
  console.log(`[INFO] 串口 ${portName} 已打开，等待上位机连接...`);
  if (mode === "modbus") {
    setupModbusProtocol(port);
  } else {
    setupJsonProtocol(port);
  }
});

port.on("error", (err) => {
  console.error(`[FATAL] 串口错误: ${err.message}`);
  process.exit(1);
});

// JSON-Lines 协议实现 (主动推流)
function setupJsonProtocol(port) {
  const parser = port.pipe(new ReadlineParser({ delimiter: "\n" }));

  // 发送遥测数据
  setInterval(() => {
    const faultNow = refreshFaultState(); // 故障注入: 与 Modbus 路径共用同一判断
    const payload = {
      deviceId: "Sensor_01",
      temperature: parseFloat(state.temperature.toFixed(1)),
      humidity: parseFloat(state.humidity.toFixed(1)),
      status: state.status,
    };

    // 阈值判定: 状态已由采样定时器置为 4 时补上错误码, 供上位机进入报警分支
    if (state.status === 4) {
      payload.err_code = 302; // ErrorCode.ThresholdExceeded
    }

    // 故障注入: 窗口期内强制故障态并附加指定错误码 (退出后自动恢复阈值推导)
    if (faultNow) {
      payload.status = 4;
      payload.err_code = faultConfig.code;
    }

    const jsonStr = JSON.stringify(payload);
    port.write(jsonStr + "\n");
    console.log(`[TX] ⬆️ ${jsonStr}`);
  }, 1000);

  // 接收下发命令
  parser.on("data", (line) => {
    const trimmed = line.trim();
    if (!trimmed) return;
    console.log(`[RX] ⬇️ ${trimmed}`);

    try {
      const cmd = JSON.parse(trimmed);
      // DeviceAction: Reset = 3, Configure = 4
      if (cmd.action === 3) {
        state.temperature = 25.0;
        state.humidity = 45.0;
        console.log("[EXEC] 设备复位 -> 恢复初始状态");
      } else if (cmd.action === 4 && cmd.parameters !== undefined) {
        state.threshold = cmd.parameters;
        console.log(`[EXEC] 更新阈值 -> ${state.threshold}`);
      }
    } catch (err) {
      console.warn(`[WARN] 忽略非标准 JSON: ${trimmed}`);
    }
  });
}

// Modbus RTU 从站: 一问一答, 只处理 8 字节固定长度的 FC03 / FC06 请求
function setupModbusProtocol(port) {
  let rxBuffer = Buffer.alloc(0);

  port.on("data", (chunk) => {
    rxBuffer = Buffer.concat([rxBuffer, chunk]);

    // FC03 与 FC06 请求帧都恰好 8 字节, 不足时等下一批数据
    while (rxBuffer.length >= 8) {
      const frame = rxBuffer.subarray(0, 8);

      // CRC 覆盖前 6 字节, 帧尾按小端存放
      const calculatedCrc = calcCrc16(frame.subarray(0, 6));
      const receivedCrc = frame.readUInt16LE(6);

      if (calculatedCrc === receivedCrc) {
        handleModbusRequest(port, frame);
        rxBuffer = rxBuffer.subarray(8);
      } else {
        // 校验失败: 只丢弃 1 字节, 让真正的帧头有机会重新对齐
        rxBuffer = rxBuffer.subarray(1);
      }
    }
  });
}

function handleModbusRequest(port, frame) {
  const faultNow = refreshFaultState();
  const slaveId = frame[0];
  const functionCode = frame[1];
  const startAddr = frame.readUInt16BE(2);
  // 同一位置在 FC03 是寄存器数量, 在 FC06 是写入值
  const valueOrCount = frame.readUInt16BE(4);

  if (slaveId !== 0x01) return;

  if (functionCode === 0x03) {
    const byteCount = valueOrCount * 2;
    const response = Buffer.alloc(3 + byteCount + 2);

    response[0] = slaveId;
    response[1] = functionCode;
    response[2] = byteCount;

    let offset = 3;
    for (let i = 0; i < valueOrCount; i++) {
      const addr = startAddr + i;
      let val = 0;
      // 寄存器映射: 0 温度×10, 1 湿度×10, 2 设备状态, 3 错误码; 未定义地址返回 0
      if (addr === 0)
        val = Math.round(state.temperature * 10);
      else if (addr === 1)
        val = Math.round(state.humidity * 10);
      else if (addr === 2)
        val = faultNow ? 4 : state.status;
      else if (addr === 3)
        val = faultNow ? faultConfig.code : state.status === 4 ? 302 : 0;

      response.writeUInt16BE(val, offset);
      offset += 2;
    }

    const crc = calcCrc16(response.subarray(0, offset));
    response.writeUInt16LE(crc, offset);
    port.write(response);

    console.log(
      `[Modbus TX] 读寄存器响应 -> ${response.toString("hex").toUpperCase()}`,
    );
  } else if (functionCode === 0x06) {
    const addrName =
      startAddr === 2
        ? "报警阈值"
        : startAddr === 1
          ? "复位指令"
          : `寄存器${startAddr}`;
    console.log(
      `[Modbus RX] 收到写寄存器指令 (${addrName}, Val: ${valueOrCount})`,
    );

    // 寄存器 2: 阈值, 两端约定按 ×10 定点传输
    if (startAddr === 2) {
      state.threshold = valueOrCount / 10.0;
      console.log(`[EXEC] 更新阈值 -> ${state.threshold}°C`);
    }
    // 寄存器 1 写 1: 复位
    else if (startAddr === 1 && valueOrCount === 1) {
      state.temperature = 25.0;
      state.humidity = 45.0;
      state.status = 1;
      console.log("[EXEC] 设备复位 -> 恢复初始状态");
    }

    // FC06 的正常响应就是原样回显请求帧
    port.write(frame);
  }
}

// CRC16(Modbus): 初值 0xFFFF, 反射多项式 0xA001; 与上位机 ModbusFrameUtility.CalcCRC16 必须逐位一致
function calcCrc16(buffer) {
  let crc = 0xffff;
  for (let i = 0; i < buffer.length; i++) {
    crc ^= buffer[i];
    for (let j = 0; j < 8; j++) {
      if ((crc & 0x0001) !== 0) {
        crc >>= 1;
        crc ^= 0xa001;
      } else {
        crc >>= 1;
      }
    }
  }
  return crc;
}
