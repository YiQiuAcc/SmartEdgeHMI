#include <Arduino.h>
#include <ESP8266WiFi.h>
#include <ESP8266WebServer.h>
#include "secrets.h"

/*
 * SmartEdgeHMI 边缘设备固件(ESP8266)
 *
 * 三件事: 仿真一路温湿度并维护设备状态、按 Modbus RTU 从站响应上位机轮询、提供一个只读网页面板。
 * 上位机与本固件的接口契约集中在寄存器映射上, 见 processModbusFrame 的注释;
 * 改动寄存器地址、缩放系数或状态码含义都必须同步 SmartEdgeHMI 侧的 Common/AppConstants.cs 与 Enums.cs。
 */

#define SLAVE_ID 1

// 板载 LED
#define LED_PIN LED_BUILTIN
#define LED_ON LOW
#define LED_OFF HIGH

// Wi-Fi 连接参数
#define WIFI_CONNECT_TIMEOUT_MS 15000 // 连接超时, 单位 ms
#define LOOP_YIELD_MS 10              // 主循环每次让出 CPU 给协议栈的时间, 单位 ms

ESP8266WebServer server(80);
bool serverStarted = false;

// 仿真状态: 温湿度由 updateSensors 随机游走, 状态码随阈值判定
float state_temperature = 25.0;
float state_humidity = 45.0;
uint16_t state_status = 1;    // 1 正常, 4 报警(对应 DeviceStatus.Fault)
float state_threshold = 80.0; // 报警阈值, 单位 °C, 掉电不保留
uint16_t state_led = 0;       // 0 熄灭, 1 点亮

// 串口接收缓冲: 单帧最长 64 字节, 足够覆盖 FC03/FC06 请求
uint8_t rxBuffer[64];
uint8_t rxIndex = 0;
unsigned long lastCharTime = 0;
const unsigned long MODBUS_T35_TIMEOUT = 5; // 帧间隔阈值(ms), 超过即认为一帧结束

// 采样节拍
unsigned long lastSampleTime = 0;
const unsigned long SAMPLE_INTERVAL = 1000; // 每 1s 更新一次仿真值

// Modbus CRC16(多项式 0xA001), 与上位机 ModbusFrameUtility.CalcCRC16 等价
uint16_t calculateCRC(uint8_t *buffer, uint16_t length)
{
  uint16_t crc = 0xFFFF;
  for (uint16_t i = 0; i < length; i++)
  {
    crc ^= buffer[i];
    for (uint8_t j = 0; j < 8; j++)
    {
      if (crc & 0x0001)
        crc = (crc >> 1) ^ 0xA001;
      else
        crc >>= 1;
    }
  }
  return crc;
}

// 模拟温湿度自然波动
void updateSensors()
{
  state_temperature += (random(-250, 251) / 1000.0);
  state_humidity += (random(-600, 601) / 1000.0);

  if (state_temperature < 10.0)
    state_temperature = 10.0;
  if (state_temperature > 60.0)
    state_temperature = 60.0;
  if (state_humidity < 20.0)
    state_humidity = 20.0;
  if (state_humidity > 95.0)
    state_humidity = 95.0;

  if (state_temperature > state_threshold)
  {
    state_status = 4;
  }
  else
  {
    state_status = 1;
  }
}

// 设备自带的网页面板: 纯前端 HTML/CSS/JS, 通过 /api/data 轮询刷新, 不参与上位机通信链路
// PROGMEM 存放, 避免占用本就紧张的 RAM
const char INDEX_HTML[] PROGMEM = R"rawliteral(
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>ESP8266 实时状态面板</title>
    <style>
        body { font-family: system-ui,-apple-system,sans-serif; background: #0f172a; color: #f8fafc; margin: 0; padding: 20px; display: flex; justify-content: center; }
        .box { max-width: 600px; width: 100%; margin-top: 20px; }
        h2 { color: #38bdf8; border-bottom: 1px solid #334155; padding-bottom: 10px; margin-bottom: 20px; }
        .grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: 15px; }
        .card { background: #1e293b; padding: 20px; border-radius: 12px; border: 1px solid #334155; }
        .full-width { grid-column: span 2; }
        .label { color: #94a3b8; font-size: 14px; margin-bottom: 5px; }
        .val { font-size: 32px; font-weight: bold; font-family: monospace; }
        .unit { font-size: 16px; color: #64748b; margin-left: 4px; }
        .status-bar { margin-top: 20px; background: #1e293b; padding: 12px; border-radius: 8px; font-size: 13px; text-align: center; color: #64748b; border: 1px solid #334155; }
    </style>
</head>
<body>
    <div class="box">
        <h2>ESP8266 节点物理层看板</h2>
        <div class="grid">
            <div class="card"><div class="label">当前温度</div><div class="val" id="t">--.-<span class="unit">°C</span></div></div>
            <div class="card"><div class="label">当前湿度</div><div class="val" id="h">--.-<span class="unit">%</span></div></div>
            <div class="card"><div class="label">设定阈值</div><div class="val" id="th">--.-<span class="unit">°C</span></div></div>
            <div class="card"><div class="label">系统状态</div><div class="val" id="s" style="font-size:20px;line-height:40px;">未知</div></div>
            <div class="card full-width"><div class="label">板载 LED 状态 (寄存器地址 4)</div><div class="val" id="led" style="font-size:20px;line-height:40px;">未知</div></div>
        </div>
        <div class="status-bar">数据自动异步刷新中 (频率: 1Hz)</div>
    </div>
    <script>
        function refresh() {
            fetch('/api/data').then(res => res.json()).then(d => {
                document.getElementById('t').innerHTML = d.temp.toFixed(1) + '<span class="unit">°C</span>';
                document.getElementById('h').innerHTML = d.humi.toFixed(1) + '<span class="unit">%</span>';
                document.getElementById('th').innerHTML = d.thrh.toFixed(1) + '<span class="unit">°C</span>';

                const sBox = document.getElementById('s');
                if(d.stat === 4) { sBox.innerText = "异常报警"; sBox.style.color = "#f43f5e"; }
                else { sBox.innerText = "运行正常"; sBox.style.color = "#10b981"; }

                const ledBox = document.getElementById('led');
                if(d.led === 1) { ledBox.innerText = "动作中 (点亮)"; ledBox.style.color = "#eab308"; }
                else { ledBox.innerText = "静态 (熄灭)"; ledBox.style.color = "#64748b"; }
            });
        }
        setInterval(refresh, 1000); refresh();
    </script>
</body>
</html>
)rawliteral";

// 提供给前端网页请求的 JSON 数据接口
void handleJsonApi()
{
  String json = "{";
  json += "\"temp\":" + String(state_temperature, 1) + ",";
  json += "\"humi\":" + String(state_humidity, 1) + ",";
  json += "\"stat\":" + String(state_status) + ",";
  json += "\"thrh\":" + String(state_threshold, 1) + ",";
  json += "\"led\":" + String(state_led);
  json += "}";
  server.send(200, "application/json", json);
}

void handleRootPage()
{
  server.send_P(200, "text/html", INDEX_HTML);
}

/*
 * Modbus RTU 从站处理: 请求已按 T3.5 断帧收集在 rxBuffer 中
 *
 * 保持寄存器映射(上位机按 FC03 读 0~3, 额外可读 4):
 *   0  温度 × 10, int16 大端, 即 253 表示 25.3 °C
 *   1  湿度 × 10, int16 大端
 *   2  设备状态, 1 正常 / 4 报警
 *   3  错误码, 报警时为 302(ThresholdExceeded), 否则 0
 *   4  LED 状态, 0 熄灭 / 1 点亮
 * FC06 写寄存器: 1 = 复位(写 1), 2 = 阈值(值/10 °C), 4 = 直接控制 LED
 */
void processModbusFrame()
{
  if (rxIndex < 8)
    return;
  if (rxBuffer[0] != SLAVE_ID)
    return;

  uint16_t receivedCRC = rxBuffer[rxIndex - 2] | (rxBuffer[rxIndex - 1] << 8);
  if (calculateCRC(rxBuffer, rxIndex - 2) != receivedCRC)
    return;

  uint8_t functionCode = rxBuffer[1];

  if (functionCode == 0x03) // FC03 读保持寄存器
  {
    uint16_t startAddress = (rxBuffer[2] << 8) | rxBuffer[3];
    uint16_t registerCount = (rxBuffer[4] << 8) | rxBuffer[5];

    // 只暴露 5 个寄存器(地址 0~4), 越界请求直接丢弃而不是返回异常码
    if ((startAddress + registerCount) > 5)
      return;

    uint8_t byteCount = registerCount * 2;
    uint8_t responseLength = 5 + byteCount;
    uint8_t response[responseLength];

    response[0] = SLAVE_ID;
    response[1] = 0x03;
    response[2] = byteCount;

    for (uint16_t i = 0; i < registerCount; i++)
    {
      uint16_t addr = startAddress + i;
      uint16_t val = 0;

      if (addr == 0)
        val = (uint16_t)round(state_temperature * 10.0);
      else if (addr == 1)
        val = (uint16_t)round(state_humidity * 10.0);
      else if (addr == 2)
        val = state_status;
      else if (addr == 3)
        val = (state_status == 4) ? 302 : 0;
      else if (addr == 4)
        val = state_led; // 支持上位机读取当前 LED 动作状态

      response[3 + (i * 2)] = (val >> 8) & 0xFF;
      response[4 + (i * 2)] = val & 0xFF;
    }

    uint16_t responseCRC = calculateCRC(response, responseLength - 2);
    response[responseLength - 2] = responseCRC & 0xFF;
    response[responseLength - 1] = (responseCRC >> 8) & 0xFF;

    Serial.write(response, responseLength);
    Serial.flush();
  }
  else if (functionCode == 0x06) // FC06 写单个寄存器
  {
    uint16_t targetAddress = (rxBuffer[2] << 8) | rxBuffer[3];
    uint16_t writeValue = (rxBuffer[4] << 8) | rxBuffer[5];

    bool executed = false;

    if (targetAddress == 1 && writeValue == 1) // 写 1 到寄存器 1 即复位
    {
      state_temperature = 25.0;
      state_humidity = 45.0;
      state_status = 1;

      // 物理动作反馈: LED 快速闪烁 3 次
      for (int i = 0; i < 3; i++)
      {
        digitalWrite(LED_PIN, LED_ON);
        delay(60);
        digitalWrite(LED_PIN, LED_OFF);
        delay(60);
      }
      state_led = 0; // 重置后维持静态熄灭
      executed = true;
    }
    else if (targetAddress == 2) // 写阈值: 值按 ×10 缩放
    {
      state_threshold = writeValue / 10.0;
      executed = true;
    }
    else if (targetAddress == 4) // 写 LED: 非 0 即点亮
    {
      state_led = (writeValue > 0) ? 1 : 0;
      digitalWrite(LED_PIN, state_led ? LED_ON : LED_OFF);
      executed = true;
    }

    if (executed)
    {
      // RTU 写操作的正常响应是原样回显请求帧
      Serial.write(rxBuffer, rxIndex);
      Serial.flush();
    }
  }
}

// 上电初始化: 串口、仿真种子、LED 与 Wi-Fi
void setup()
{
  Serial.begin(115200);
  randomSeed(analogRead(0));

  pinMode(LED_PIN, OUTPUT);
  digitalWrite(LED_PIN, LED_OFF);

  // 异步发起 Wi-Fi 连接
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

  // 丢弃上电前残留的串口字节, 否则会与第一帧拼接
  while (Serial.available() > 0)
  {
    Serial.read();
  }
  rxIndex = 0;
}

void loop()
{
  unsigned long currentMillis = millis();

  // Wi-Fi 未就绪时不启动网页服务器: 服务器一旦 begin 会占用 socket 资源, 且串口轮询不能被拖慢
  if (WiFi.status() == WL_CONNECTED && !serverStarted)
  {
    server.on("/", handleRootPage);
    server.on("/api/data", handleJsonApi);
    server.begin();
    serverStarted = true;
  }

  if (serverStarted)
  {
    server.handleClient();
  }

  // 串口读取不做阻塞等待: 每轮 loop 只读取当前可读字节
  while (Serial.available() > 0)
  {
    if (rxIndex < sizeof(rxBuffer))
    {
      rxBuffer[rxIndex++] = Serial.read();
      lastCharTime = currentMillis;
    }
    else
    {
      // 超长帧直接丢弃: 正常请求不超过 8 字节, 缓冲满只可能是噪声
      rxIndex = 0;
    }
  }

  // T3.5 断帧: 超过 5ms 无新字节即认为一帧结束。
  // 115200 8N1 下 3.5 字符约 0.3ms, 这里取 5ms 是留足 USB 转串口抖动余量的实现值, 不是严格 T3.5
  if (rxIndex > 0 && (currentMillis - lastCharTime >= MODBUS_T35_TIMEOUT))
  {
    processModbusFrame();
    rxIndex = 0;
  }

  // 仿真节拍: 每秒推进一次温湿度随机游走
  if (currentMillis - lastSampleTime >= SAMPLE_INTERVAL)
  {
    lastSampleTime = currentMillis;
    updateSensors();
  }

  delay(LOOP_YIELD_MS);
}
