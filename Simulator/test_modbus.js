import ModbusRTU from "modbus-serial";
const client = new ModbusRTU();

// ---------------- 配置参数 ----------------
const PORT_NAME = "COM6";
const BAUD_RATE = 115200;
const SLAVE_ID = 1; // 对应 main.cpp 中的 #define SLAVE_ID 1

async function run() {
  try {
    console.log(
      `[${new Date().toLocaleTimeString()}] 正在尝试打开串口 ${PORT_NAME}...`,
    );

    // 连接串口
    await client.connectRTUBuffered(PORT_NAME, {
      baudRate: BAUD_RATE,
      // 显式禁用 DTR/RTS，防止部分 Arduino 频繁复位
      dtr: false,
      rts: false,
    });

    client.setID(SLAVE_ID);
    client.setTimeout(1000); // 设置 1 秒超时
    console.log(
      `[${new Date().toLocaleTimeString()}] 串口 ${PORT_NAME} 打开成功！`,
    );

    // 【关键时序】等待 2 秒
    // CH340 在打开瞬间由于系统底层驱动原因，仍可能导致 Arduino 复位
    // 我们等 2 秒让 Arduino 完成 setup()
    console.log("等待 2 秒让下位机硬件及电平稳定...");
    await new Promise((resolve) => setTimeout(resolve, 2000));

    // 测试 1: 模拟 C# 下发阈值 (功能码 0x06, 寄存器地址 0x0002, 写入值 300)
    console.log(
      `[${new Date().toLocaleTimeString()}] 测试 1: 正在下发阈值至 0x0002 -> 300...`,
    );
    await client.writeRegister(2, 300);
    console.log("=> 下发阈值成功！下位机已正确响应 ACK。");

    // 测试 2: 启动每秒轮询 (功能码 0x03)
    console.log(
      `[${new Date().toLocaleTimeString()}] 测试 2: 启动每秒定时轮询...`,
    );

    setInterval(async () => {
      try {
        // 假设你的 main.cpp 允许读取从 0x0001 开始的寄存器
        // 这里尝试读取 3 个寄存器
        const response = await client.readHoldingRegisters(1, 3);
        console.log(
          `[${new Date().toLocaleTimeString()}] 轮询成功, 数据:`,
          response.data,
        );
      } catch (err) {
        console.error(
          `[${new Date().toLocaleTimeString()}] 轮询读失败: ${err.message}`,
        );
      }
    }, 1000);
  } catch (err) {
    console.error(`\n[致命错误] 脚本执行中断: ${err.message}`);
    if (
      err.message.includes("bad file descriptor") ||
      err.message.includes("closed")
    ) {
      console.error(
        "产生该错误通常意味着 OS 层面的串口句柄被强行关闭（类似 C# 的 EOF 0字节断开）。",
      );
    }
  }
}

// 监听底层的异常断开事件
if (client._port) {
  client._port.on("close", () => {
    console.log(`\n[事件通知] 警告：底层物理串口 ${PORT_NAME} 已断开连接！`);
  });
  client._port.on("error", (err) => {
    console.error(`\n[事件通知] 底层串口发生错误:`, err);
  });
}

run();
