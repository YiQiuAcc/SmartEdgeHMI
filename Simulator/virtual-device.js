/**
 * SmartEdgeHMI Virtual Device Simulator (双模下位机模拟器)
 * * 启动方式:
 * pnpm run sim:json    (JSON主动上报模式, 1秒/次)
 * pnpm run sim:modbus  (Modbus RTU轮询模式)
 */

import { SerialPort } from "serialport";
import { ReadlineParser } from "@serialport/parser-readline";

// 物理环境仿真引擎 (Shared State)
const state = {
  temperature: 25.0,
  humidity: 45.0,
  status: 1, // 1: 正常, 2: 报警
  threshold: 80.0, // 报警阈值
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

// 核心入口与配置
const mode = (process.argv[2] || "json").toLowerCase();
const portName = process.argv[3] || "COM2";
const baudRate = parseInt(process.argv[4] || "115200", 10);

console.log(`\n🚀 启动虚拟设备模拟器...`);
console.log(`🔌 端口: ${portName} @ ${baudRate} bps`);
console.log(
  `📡 模式: ${mode === "modbus" ? "Modbus RTU (被动轮询)" : "JSON-Lines (主动上报)"}\n`,
);

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
    const payload = {
      deviceId: "Sensor_01",
      temperature: parseFloat(state.temperature.toFixed(1)),
      humidity: parseFloat(state.humidity.toFixed(1)),
      status: state.status,
    };

    // 阈值监控：温度超过阈值时附加错误码
    if (state.status === 4) {
      payload.err_code = 302; // ErrorCode.ThresholdExceeded
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

//  Modbus RTU 协议实现 (一问一答)

function setupModbusProtocol(port) {
  let rxBuffer = Buffer.alloc(0);

  port.on("data", (chunk) => {
    // 将新数据追加到滑动窗口
    rxBuffer = Buffer.concat([rxBuffer, chunk]);

    // Modbus RTU 请求帧极小值为 8 字节 (03/06 等常用功能码)
    while (rxBuffer.length >= 8) {
      const frame = rxBuffer.subarray(0, 8);

      // 验证 CRC (与 C# 严密对应)
      const calculatedCrc = calcCrc16(frame.subarray(0, 6));
      const receivedCrc = frame.readUInt16LE(6);

      if (calculatedCrc === receivedCrc) {
        handleModbusRequest(port, frame);
        // 消费这一帧
        rxBuffer = rxBuffer.subarray(8);
      } else {
        // CRC失败，丢弃脏字节，滑动1位
        rxBuffer = rxBuffer.subarray(1);
      }
    }
  });
}

function handleModbusRequest(port, frame) {
  const slaveId = frame[0];
  const functionCode = frame[1];
  const startAddr = frame.readUInt16BE(2);
  const valueOrCount = frame.readUInt16BE(4); // 对于 03 是 Count，对于 06 是 Value

  if (slaveId !== 0x01) return; // 假设本机站号为 1

  if (functionCode === 0x03) {
    // 收到指令：03 读保持寄存器
    const byteCount = valueOrCount * 2;
    const response = Buffer.alloc(3 + byteCount + 2);

    response[0] = slaveId;
    response[1] = functionCode;
    response[2] = byteCount;

    let offset = 3;
    for (let i = 0; i < valueOrCount; i++) {
      const addr = startAddr + i;
      let val = 0;
      // 地址映射表 (上位机解析 C# 必须对齐)
      if (addr === 0)
        val = Math.round(state.temperature * 10); // 寄存器0: 温度
      else if (addr === 1)
        val = Math.round(state.humidity * 10); // 寄存器1: 湿度
      else if (addr === 2) val = state.status; // 寄存器2: 设备状态 (DeviceStatus)
      else if (addr === 3)
        val = state.status === 4 ? 302 : 0; // 寄存器3: 错误码 (ErrorCode)

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
    // 收到指令：06 写单个寄存器
    const addrName =
      startAddr === 2
        ? "报警阈值"
        : startAddr === 1
          ? "复位指令"
          : `寄存器${startAddr}`;
    console.log(
      `[Modbus RX] 收到写寄存器指令 (${addrName}, Val: ${valueOrCount})`,
    );

    // 写入寄存器 2: 更新报警阈值 (定点数×10传输)
    if (startAddr === 2) {
      state.threshold = valueOrCount / 10.0;
      console.log(`[EXEC] 更新阈值 -> ${state.threshold}°C`);
    }
    // 写入寄存器 1: 复位设备
    else if (startAddr === 1 && valueOrCount === 1) {
      state.temperature = 25.0;
      state.humidity = 45.0;
      state.status = 1;
      console.log("[EXEC] 设备复位 -> 恢复初始状态");
    }

    // 原样返回作为 ACK 响应
    port.write(frame);
  }
}

// 标准 Modbus RTU CRC16 计算函数 (与上位机 C# 端一致)
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
