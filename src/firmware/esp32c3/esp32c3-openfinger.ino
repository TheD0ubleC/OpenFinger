/*
  OpenFinger ESP32-C3 USB Serial Provisioning + WiFi UDP ADC 30FPS

  USB-only 配网：
    - 配网/配置：USB Serial
    - ADC 实时数据：WiFi UDP 主动发送到电脑
    - GPIO0~GPIO4 ADC，30FPS，每通道 8 次采样平均

  Arduino:
    Board: ESP32C3 Dev Module
    USB CDC On Boot: Enabled

  LED:
    ESP32-C3 SuperMini: GPIO8, active low
*/

#include <Arduino.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include <Preferences.h>
#include "openfinger_pin_config.h"
#include "openfinger_version.h"

#ifndef OPENFINGER_BOARD_TARGET
#define OPENFINGER_BOARD_TARGET "esp32c3"
#endif

#ifndef OPENFINGER_FIRMWARE_VERSION
#define OPENFINGER_FIRMWARE_VERSION "dev"
#endif

#ifndef OPENFINGER_REPORT_HZ
#define OPENFINGER_REPORT_HZ 30
#endif

#ifndef OPENFINGER_LED_PIN
#define OPENFINGER_LED_PIN 8
#endif

#ifndef OPENFINGER_LED_ACTIVE_LOW
#define OPENFINGER_LED_ACTIVE_LOW 1
#endif

#ifndef OPENFINGER_LED_IS_NEOPIXEL
#define OPENFINGER_LED_IS_NEOPIXEL 0
#endif

#ifndef OPENFINGER_BATTERY_ADC_PIN
#define OPENFINGER_BATTERY_ADC_PIN -1
#endif

#ifndef OPENFINGER_BATTERY_DIVIDER_RATIO
#define OPENFINGER_BATTERY_DIVIDER_RATIO 2.0f
#endif

#ifndef OPENFINGER_BATTERY_EMPTY_VOLTAGE
#define OPENFINGER_BATTERY_EMPTY_VOLTAGE 3.30f
#endif

#ifndef OPENFINGER_BATTERY_FULL_VOLTAGE
#define OPENFINGER_BATTERY_FULL_VOLTAGE 4.20f
#endif

#ifndef OPENFINGER_BATTERY_CHARGE_PIN
#define OPENFINGER_BATTERY_CHARGE_PIN -1
#endif

#ifndef OPENFINGER_BATTERY_CHARGE_ACTIVE_LOW
#define OPENFINGER_BATTERY_CHARGE_ACTIVE_LOW 1
#endif

static const int DEFAULT_ADC_PINS[5] = {
  OPENFINGER_ADC_PIN_THUMB,
  OPENFINGER_ADC_PIN_INDEX,
  OPENFINGER_ADC_PIN_MIDDLE,
  OPENFINGER_ADC_PIN_RING,
  OPENFINGER_ADC_PIN_PINKY
};
static const uint8_t DEFAULT_ADC_MASK = 0b11111;
static const int DEFAULT_TRACKING_SWITCH_PIN = OPENFINGER_TRACKING_SWITCH_PIN;
static const int DEFAULT_TRACKING_SWITCH_MODE = OPENFINGER_TRACKING_SWITCH_MODE;
static const int DEFAULT_JOYSTICK_VRX_PIN = OPENFINGER_JOYSTICK_VRX_PIN;
static const int DEFAULT_JOYSTICK_VRY_PIN = OPENFINGER_JOYSTICK_VRY_PIN;
static const int DEFAULT_JOYSTICK_SW_PIN = OPENFINGER_JOYSTICK_SW_PIN;
static const int DEFAULT_BATTERY_ADC_PIN = OPENFINGER_BATTERY_ADC_PIN;
static const int DEFAULT_BATTERY_CHARGE_PIN = OPENFINGER_BATTERY_CHARGE_PIN;
static const uint32_t DEFAULT_ADC_FPS = OPENFINGER_REPORT_HZ;
static const int ADC_SAMPLES = 8;

int adcPins[5] = {
  OPENFINGER_ADC_PIN_THUMB,
  OPENFINGER_ADC_PIN_INDEX,
  OPENFINGER_ADC_PIN_MIDDLE,
  OPENFINGER_ADC_PIN_RING,
  OPENFINGER_ADC_PIN_PINKY
};
int trackingSwitchPin = OPENFINGER_TRACKING_SWITCH_PIN;
int trackingSwitchMode = OPENFINGER_TRACKING_SWITCH_MODE;
int joystickVrxPin = OPENFINGER_JOYSTICK_VRX_PIN;
int joystickVryPin = OPENFINGER_JOYSTICK_VRY_PIN;
int joystickSwPin = OPENFINGER_JOYSTICK_SW_PIN;
int batteryAdcPin = OPENFINGER_BATTERY_ADC_PIN;
int batteryChargePin = OPENFINGER_BATTERY_CHARGE_PIN;
uint32_t adcFps = OPENFINGER_REPORT_HZ;
uint32_t adcIntervalMs = 1000 / OPENFINGER_REPORT_HZ;

Preferences prefs;
WiFiUDP udp;

String deviceName;
String serialLine;

String currentState = "booting";
String currentMessage = "";

String pendingSSID;
String pendingPassword;
bool pendingSave = true;
bool connectRequested = false;
unsigned long connectStartMs = 0;
const unsigned long CONNECT_TIMEOUT_MS = 25000;

IPAddress hostIp;
uint16_t hostUdpPort = 39001;
uint8_t adcMask = DEFAULT_ADC_MASK;
String deviceRole = "unknown";
bool udpTargetValid = false;
bool adcStreaming = false;

uint32_t adcSeq = 0;
unsigned long lastAdcMs = 0;
bool trackingEnabledCached = true;
int trackingSwitchRawCached = HIGH;
unsigned long trackingSwitchLastEdgeMs = 0;
bool joystickSwPressedCached = false;
const unsigned long TRACKING_SWITCH_DEBOUNCE_MS = 35;
const unsigned long HEARTBEAT_INTERVAL_MS = 1000;
const int BATTERY_SAMPLES = 8;

bool identifyActive = false;
bool identifyLedOn = false;
uint8_t identifyTogglesLeft = 0;
unsigned long identifyLastMs = 0;
const unsigned long IDENTIFY_INTERVAL_MS = 160;
unsigned long lastHeartbeatMs = 0;

static void ledSet(bool on) {
#if OPENFINGER_LED_IS_NEOPIXEL
  if (on) neopixelWrite(OPENFINGER_LED_PIN, 0, 40, 0);
  else neopixelWrite(OPENFINGER_LED_PIN, 0, 0, 0);
#else
  bool level = on;
#if OPENFINGER_LED_ACTIVE_LOW
  level = !level;
#endif
  digitalWrite(OPENFINGER_LED_PIN, level ? HIGH : LOW);
#endif
}

static void ledInit() {
#if !OPENFINGER_LED_IS_NEOPIXEL
  pinMode(OPENFINGER_LED_PIN, OUTPUT);
#endif
  ledSet(false);
}

static void setConnectedLed() {
  ledSet(WiFi.status() == WL_CONNECTED);
}

static const char* trackingSwitchModeName() {
  switch (trackingSwitchMode) {
    case OPENFINGER_TRACKING_SWITCH_MODE_ACTIVE_HIGH_PULLDOWN:
      return "active_high_pulldown";
    case OPENFINGER_TRACKING_SWITCH_MODE_ACTIVE_LOW_PULLUP:
      return "active_low_pullup";
    default:
      return "disabled";
  }
}

static void recalculateAdcSchedule() {
  if (adcFps < 10) adcFps = 10;
  if (adcFps > 240) adcFps = 240;
  adcIntervalMs = max(1UL, 1000UL / adcFps);
}

static void trackingSwitchInit() {
  if (trackingSwitchPin < 0 || trackingSwitchMode == OPENFINGER_TRACKING_SWITCH_MODE_DISABLED) {
    trackingEnabledCached = true;
    return;
  }

  if (trackingSwitchMode == OPENFINGER_TRACKING_SWITCH_MODE_ACTIVE_HIGH_PULLDOWN) {
    pinMode(trackingSwitchPin, INPUT_PULLDOWN);
  } else if (trackingSwitchMode == OPENFINGER_TRACKING_SWITCH_MODE_ACTIVE_LOW_PULLUP) {
    pinMode(trackingSwitchPin, INPUT_PULLUP);
  } else {
    pinMode(trackingSwitchPin, INPUT);
  }

  trackingSwitchRawCached = digitalRead(trackingSwitchPin);
  trackingSwitchLastEdgeMs = millis();
}

static bool decodeTrackingEnabledFromLevel(int level) {
  if (trackingSwitchMode == OPENFINGER_TRACKING_SWITCH_MODE_ACTIVE_HIGH_PULLDOWN) {
    return level == HIGH;
  }
  if (trackingSwitchMode == OPENFINGER_TRACKING_SWITCH_MODE_ACTIVE_LOW_PULLUP) {
    return level == LOW;
  }
  return true;
}

static bool readTrackingEnabled() {
  if (trackingSwitchPin < 0 || trackingSwitchMode == OPENFINGER_TRACKING_SWITCH_MODE_DISABLED) {
    trackingEnabledCached = true;
    return true;
  }

  unsigned long now = millis();
  int level = digitalRead(trackingSwitchPin);
  if (level != trackingSwitchRawCached) {
    trackingSwitchRawCached = level;
    trackingSwitchLastEdgeMs = now;
  } else if (now - trackingSwitchLastEdgeMs >= TRACKING_SWITCH_DEBOUNCE_MS) {
    trackingEnabledCached = decodeTrackingEnabledFromLevel(level);
  }

  return trackingEnabledCached;
}

static void joystickSwitchInit() {
  if (joystickSwPin < 0) {
    joystickSwPressedCached = false;
    return;
  }

  pinMode(joystickSwPin, INPUT_PULLUP);
  joystickSwPressedCached = (digitalRead(joystickSwPin) == LOW);
}

static void batteryInit() {
  if (batteryChargePin >= 0) {
    pinMode(batteryChargePin, INPUT_PULLUP);
  }
}

static bool batteryTelemetryAvailable() {
  return batteryAdcPin >= 0;
}

static int readBatteryMillivolts() {
  if (!batteryTelemetryAvailable()) {
    return -1;
  }

  long sum = 0;
  for (int i = 0; i < BATTERY_SAMPLES; i++) {
    sum += analogReadMilliVolts(batteryAdcPin);
    delayMicroseconds(150);
  }

  float pinMillivolts = (float)sum / (float)BATTERY_SAMPLES;
  float batteryMillivolts = pinMillivolts * OPENFINGER_BATTERY_DIVIDER_RATIO;
  return (int)(batteryMillivolts + 0.5f);
}

static int batteryPercentFromMillivolts(int batteryMv) {
  if (batteryMv <= 0) {
    return -1;
  }

  const float voltage = (float)batteryMv / 1000.0f;
  const float span = OPENFINGER_BATTERY_FULL_VOLTAGE - OPENFINGER_BATTERY_EMPTY_VOLTAGE;
  if (span <= 0.01f) {
    return -1;
  }

  float normalized = (voltage - OPENFINGER_BATTERY_EMPTY_VOLTAGE) / span;
  if (normalized < 0.0f) normalized = 0.0f;
  if (normalized > 1.0f) normalized = 1.0f;
  return (int)(normalized * 100.0f + 0.5f);
}

static bool batteryChargingKnown() {
  return batteryChargePin >= 0;
}

static bool readBatteryCharging() {
  if (!batteryChargingKnown()) {
    return false;
  }

  int level = digitalRead(batteryChargePin);
#if OPENFINGER_BATTERY_CHARGE_ACTIVE_LOW
  return level == LOW;
#else
  return level == HIGH;
#endif
}

static int readJoystickSwitchFlag() {
  if (joystickSwPin < 0) {
    return -1;
  }

  joystickSwPressedCached = (digitalRead(joystickSwPin) == LOW);
  return joystickSwPressedCached ? 1 : 0;
}

static void startIdentifyBlink() {
  identifyActive = true;
  identifyLedOn = false;
  identifyTogglesLeft = 24;  // 12 flashes
  identifyLastMs = 0;
}

static void updateIdentifyBlink() {
  if (!identifyActive) return;

  unsigned long now = millis();
  if (identifyLastMs != 0 && now - identifyLastMs < IDENTIFY_INTERVAL_MS) return;

  identifyLastMs = now;
  identifyLedOn = !identifyLedOn;
  ledSet(identifyLedOn);

  if (identifyTogglesLeft > 0) identifyTogglesLeft--;

  if (identifyTogglesLeft == 0) {
    identifyActive = false;
    identifyLedOn = false;
    setConnectedLed();
  }
}

static int hexValue(char c) {
  if (c >= '0' && c <= '9') return c - '0';
  if (c >= 'a' && c <= 'f') return 10 + c - 'a';
  if (c >= 'A' && c <= 'F') return 10 + c - 'A';
  return -1;
}

static String hexDecode(const String& hex) {
  String out;
  out.reserve(hex.length() / 2);
  for (int i = 0; i + 1 < (int)hex.length(); i += 2) {
    int hi = hexValue(hex[i]);
    int lo = hexValue(hex[i + 1]);
    if (hi < 0 || lo < 0) continue;
    out += (char)((hi << 4) | lo);
  }
  return out;
}

static String urlDecode(const String& s) {
  String out;
  out.reserve(s.length());
  for (int i = 0; i < (int)s.length(); i++) {
    char c = s[i];
    if (c == '%' && i + 2 < (int)s.length()) {
      int hi = hexValue(s[i + 1]);
      int lo = hexValue(s[i + 2]);
      if (hi >= 0 && lo >= 0) {
        out += (char)((hi << 4) | lo);
        i += 2;
      } else {
        out += c;
      }
    } else if (c == '+') {
      out += ' ';
    } else {
      out += c;
    }
  }
  return out;
}

static String getParam(const String& query, const String& key) {
  String prefix = key + "=";
  int start = 0;

  while (start < (int)query.length()) {
    int end = query.indexOf('&', start);
    if (end < 0) end = query.length();

    String part = query.substring(start, end);
    if (part.startsWith(prefix)) {
      return urlDecode(part.substring(prefix.length()));
    }

    start = end + 1;
  }

  return "";
}

static String escapeJson(const String& s) {
  String out;
  out.reserve(s.length() + 8);
  for (int i = 0; i < (int)s.length(); i++) {
    char c = s[i];
    if (c == '\\') out += "\\\\";
    else if (c == '"') out += "\\\"";
    else if (c == '\n') out += "\\n";
    else if (c == '\r') out += "\\r";
    else if ((uint8_t)c < 0x20) out += "?";
    else out += c;
  }
  return out;
}

static String statusJson() {
  String json = "{";
  bool trackingEnabled = readTrackingEnabled();
  int batteryMv = readBatteryMillivolts();
  int batteryPercent = batteryPercentFromMillivolts(batteryMv);
  bool hasBattery = batteryTelemetryAvailable() && batteryMv > 0;
  bool chargingKnown = batteryChargingKnown();
  bool charging = readBatteryCharging();
  json += "\"device\":\"" + escapeJson(deviceName) + "\"";
  json += ",\"state\":\"" + escapeJson(currentState) + "\"";
  json += ",\"message\":\"" + escapeJson(currentMessage) + "\"";
  json += ",\"mac\":\"" + WiFi.macAddress() + "\"";
  json += ",\"sta_ip\":\"" + WiFi.localIP().toString() + "\"";
  json += ",\"wifi_connected\":" + String(WiFi.status() == WL_CONNECTED ? "true" : "false");
  json += ",\"host_ip\":\"" + (udpTargetValid ? hostIp.toString() : String("")) + "\"";
  json += ",\"udp_port\":" + String(hostUdpPort);
  json += ",\"adc_mask\":" + String(adcMask);
  json += ",\"role\":\"" + escapeJson(deviceRole) + "\"";
  json += ",\"board_target\":\"" + String(OPENFINGER_BOARD_TARGET) + "\"";
  json += ",\"firmware_version\":\"" + escapeJson(String(OPENFINGER_FIRMWARE_VERSION)) + "\"";
  json += ",\"report_hz\":" + String(adcFps);
  json += ",\"thumb_pin\":" + String(adcPins[0]);
  json += ",\"index_pin\":" + String(adcPins[1]);
  json += ",\"middle_pin\":" + String(adcPins[2]);
  json += ",\"ring_pin\":" + String(adcPins[3]);
  json += ",\"pinky_pin\":" + String(adcPins[4]);
  json += ",\"tracking_enabled\":" + String(trackingEnabled ? "true" : "false");
  json += ",\"tracking_switch_pin\":" + String(trackingSwitchPin);
  json += ",\"tracking_switch_mode\":\"" + String(trackingSwitchModeName()) + "\"";
  json += ",\"joystick_vrx_pin\":" + String(joystickVrxPin);
  json += ",\"joystick_vry_pin\":" + String(joystickVryPin);
  json += ",\"joystick_sw_pin\":" + String(joystickSwPin);
  json += ",\"battery_adc_pin\":" + String(batteryAdcPin);
  json += ",\"battery_charge_pin\":" + String(batteryChargePin);
  json += ",\"protocol_version\":\"" + String(OPENFINGER_PROTOCOL_VERSION) + "\"";
  json += ",\"capabilities\":\"runtime_pins,runtime_report_hz,joystick_gpio,joystick_runtime,finger_pins,battery_status\"";
  json += ",\"adc_streaming\":" + String(adcStreaming ? "true" : "false");
  json += ",\"battery_available\":" + String(hasBattery ? "true" : "false");
  json += ",\"battery_mv\":" + String(hasBattery ? batteryMv : -1);
  json += ",\"battery_percent\":" + String(hasBattery ? batteryPercent : -1);
  json += ",\"battery_charging_known\":" + String(chargingKnown ? "true" : "false");
  json += ",\"battery_charging\":" + String(charging ? "true" : "false");
  json += ",\"seq\":" + String(adcSeq);
  json += "}";
  return json;
}

static void printSerialStatus() {
  Serial.print("OFSTATUS ");
  Serial.println(statusJson());
}

static void setStatus(const String& state, const String& message = "") {
  currentState = state;
  currentMessage = message;
  printSerialStatus();
}

static bool parseHostIp(const String& ipText) {
  if (ipText.length() == 0 || ipText == "auto") return false;

  IPAddress ip;
  if (!ip.fromString(ipText)) return false;

  hostIp = ip;
  udpTargetValid = true;
  return true;
}

static void resetHardwareConfigToDefaults() {
  for (int i = 0; i < 5; i++) {
    adcPins[i] = DEFAULT_ADC_PINS[i];
  }

  trackingSwitchPin = DEFAULT_TRACKING_SWITCH_PIN;
  trackingSwitchMode = DEFAULT_TRACKING_SWITCH_MODE;
  joystickVrxPin = DEFAULT_JOYSTICK_VRX_PIN;
  joystickVryPin = DEFAULT_JOYSTICK_VRY_PIN;
  joystickSwPin = DEFAULT_JOYSTICK_SW_PIN;
  batteryAdcPin = DEFAULT_BATTERY_ADC_PIN;
  batteryChargePin = DEFAULT_BATTERY_CHARGE_PIN;
  adcFps = DEFAULT_ADC_FPS;
  recalculateAdcSchedule();
}

static void saveRuntimeConfig() {
  prefs.begin("openfinger", false);
  prefs.putUShort("udp_port", hostUdpPort);
  prefs.putUChar("adc_mask", adcMask);
  prefs.putString("host_ip", udpTargetValid ? hostIp.toString() : "");
  prefs.putString("role", deviceRole);
  prefs.putInt("report_hz", (int)adcFps);
  prefs.putInt("thumb_pin", adcPins[0]);
  prefs.putInt("index_pin", adcPins[1]);
  prefs.putInt("middle_pin", adcPins[2]);
  prefs.putInt("ring_pin", adcPins[3]);
  prefs.putInt("pinky_pin", adcPins[4]);
  prefs.putInt("tracking_pin", trackingSwitchPin);
  prefs.putInt("tracking_mode", trackingSwitchMode);
  prefs.putInt("joystick_vrx", joystickVrxPin);
  prefs.putInt("joystick_vry", joystickVryPin);
  prefs.putInt("joystick_sw", joystickSwPin);
  prefs.putInt("battery_adc", batteryAdcPin);
  prefs.putInt("battery_charge", batteryChargePin);
  prefs.end();
}

static void loadRuntimeConfig() {
  resetHardwareConfigToDefaults();
  prefs.begin("openfinger", true);
  hostUdpPort = prefs.getUShort("udp_port", 39001);
  String host = prefs.getString("host_ip", "");
  deviceRole = prefs.getString("role", "unknown");
  adcMask = prefs.getUChar("adc_mask", DEFAULT_ADC_MASK);
  adcFps = (uint32_t)prefs.getInt("report_hz", (int)DEFAULT_ADC_FPS);
  adcPins[0] = prefs.getInt("thumb_pin", DEFAULT_ADC_PINS[0]);
  adcPins[1] = prefs.getInt("index_pin", DEFAULT_ADC_PINS[1]);
  adcPins[2] = prefs.getInt("middle_pin", DEFAULT_ADC_PINS[2]);
  adcPins[3] = prefs.getInt("ring_pin", DEFAULT_ADC_PINS[3]);
  adcPins[4] = prefs.getInt("pinky_pin", DEFAULT_ADC_PINS[4]);
  trackingSwitchPin = prefs.getInt("tracking_pin", DEFAULT_TRACKING_SWITCH_PIN);
  trackingSwitchMode = prefs.getInt("tracking_mode", DEFAULT_TRACKING_SWITCH_MODE);
  joystickVrxPin = prefs.getInt("joystick_vrx", DEFAULT_JOYSTICK_VRX_PIN);
  joystickVryPin = prefs.getInt("joystick_vry", DEFAULT_JOYSTICK_VRY_PIN);
  joystickSwPin = prefs.getInt("joystick_sw", DEFAULT_JOYSTICK_SW_PIN);
  batteryAdcPin = prefs.getInt("battery_adc", DEFAULT_BATTERY_ADC_PIN);
  batteryChargePin = prefs.getInt("battery_charge", DEFAULT_BATTERY_CHARGE_PIN);
  prefs.end();

  if (adcMask == 0) {
    adcMask = DEFAULT_ADC_MASK;
  }
  recalculateAdcSchedule();

  if (host.length()) {
    IPAddress ip;
    if (ip.fromString(host)) {
      hostIp = ip;
      udpTargetValid = true;
    }
  }
}

static bool applyAdcConfig(const String& query) {
  String host = getParam(query, "host_ip");
  String port = getParam(query, "udp_port");
  String role = getParam(query, "role");
  String reportHzText = getParam(query, "report_hz");
  String thumbPinText = getParam(query, "thumb_pin");
  String indexPinText = getParam(query, "index_pin");
  String middlePinText = getParam(query, "middle_pin");
  String ringPinText = getParam(query, "ring_pin");
  String pinkyPinText = getParam(query, "pinky_pin");
  String trackingPinText = getParam(query, "tracking_switch_pin");
  String trackingModeText = getParam(query, "tracking_switch_mode");
  String joystickVrxText = getParam(query, "joystick_vrx_pin");
  String joystickVryText = getParam(query, "joystick_vry_pin");
  String joystickSwText = getParam(query, "joystick_sw_pin");
  String batteryAdcText = getParam(query, "battery_adc_pin");
  String batteryChargeText = getParam(query, "battery_charge_pin");

  if (role.length() > 0) {
    role.toLowerCase();
    if (role == "left" || role == "right" || role == "unknown") {
      deviceRole = role;
    } else {
      setStatus("error", "Invalid role");
      return false;
    }
  }

  if (host.length() > 0) {
    if (!parseHostIp(host)) {
      setStatus("error", "Invalid host_ip; use a real LAN IP");
      return false;
    }
  }

  if (port.length() > 0) {
    int p = port.toInt();
    if (p > 0 && p < 65536) hostUdpPort = (uint16_t)p;
  }

  if (reportHzText.length() > 0) {
    int parsedReportHz = reportHzText.toInt();
    if (parsedReportHz < 10 || parsedReportHz > 240) {
      setStatus("error", "Invalid report_hz");
      return false;
    }
    adcFps = (uint32_t)parsedReportHz;
    recalculateAdcSchedule();
  }

  if (thumbPinText.length() > 0) adcPins[0] = thumbPinText.toInt();
  if (indexPinText.length() > 0) adcPins[1] = indexPinText.toInt();
  if (middlePinText.length() > 0) adcPins[2] = middlePinText.toInt();
  if (ringPinText.length() > 0) adcPins[3] = ringPinText.toInt();
  if (pinkyPinText.length() > 0) adcPins[4] = pinkyPinText.toInt();
  if (trackingPinText.length() > 0) trackingSwitchPin = trackingPinText.toInt();
  if (joystickVrxText.length() > 0) joystickVrxPin = joystickVrxText.toInt();
  if (joystickVryText.length() > 0) joystickVryPin = joystickVryText.toInt();
  if (joystickSwText.length() > 0) joystickSwPin = joystickSwText.toInt();
  if (batteryAdcText.length() > 0) batteryAdcPin = batteryAdcText.toInt();
  if (batteryChargeText.length() > 0) batteryChargePin = batteryChargeText.toInt();

  if (trackingModeText.length() > 0) {
    trackingModeText.toLowerCase();
    if (trackingModeText == "disabled") {
      trackingSwitchMode = OPENFINGER_TRACKING_SWITCH_MODE_DISABLED;
    } else if (trackingModeText == "active_low_pullup") {
      trackingSwitchMode = OPENFINGER_TRACKING_SWITCH_MODE_ACTIVE_LOW_PULLUP;
    } else if (trackingModeText == "active_high_pulldown") {
      trackingSwitchMode = OPENFINGER_TRACKING_SWITCH_MODE_ACTIVE_HIGH_PULLDOWN;
    } else {
      setStatus("error", "Invalid tracking_switch_mode");
      return false;
    }
  }

  if (trackingSwitchPin < 0) {
    trackingSwitchMode = OPENFINGER_TRACKING_SWITCH_MODE_DISABLED;
  }

  trackingSwitchInit();
  joystickSwitchInit();
  batteryInit();
  adcMask = DEFAULT_ADC_MASK;

  adcStreaming = (WiFi.status() == WL_CONNECTED && udpTargetValid);
  saveRuntimeConfig();

  setStatus(adcStreaming ? "streaming" : "configured", "Device config updated");
  return true;
}

static void startWifiConnect(const String& ssid, const String& password, bool saveCreds) {
  pendingSSID = ssid;
  pendingPassword = password;
  pendingSave = saveCreds;

  if (pendingSSID.length() == 0) {
    setStatus("error", "SSID is empty");
    return;
  }

  adcStreaming = false;
  ledSet(false);

  WiFi.mode(WIFI_STA);
  WiFi.setSleep(false);
  WiFi.disconnect(false, false);
  delay(100);

  setStatus("connecting", "Connecting to " + pendingSSID);
  WiFi.begin(pendingSSID.c_str(), pendingPassword.c_str());

  connectStartMs = millis();
  connectRequested = true;
}

static void processProvisionPayload(const String& query) {
  String ssid = getParam(query, "ssid");
  String password = getParam(query, "password");
  String save = getParam(query, "save");

  if (ssid.length() == 0) {
    setStatus("error", "Provision payload missing ssid");
    return;
  }

  if (!applyAdcConfig(query)) return;

  bool saveCreds = (save != "0");
  startWifiConnect(ssid, password, saveCreds);
}

static void sendAdcFrame() {
  if (WiFi.status() != WL_CONNECTED || !udpTargetValid) return;

  unsigned long now = millis();
  if (now - lastAdcMs < adcIntervalMs) return;
  lastAdcMs = now;

  int raw[5] = {0, 0, 0, 0, 0};
  bool trackingEnabled = readTrackingEnabled();
  int joystickRawX = -1;
  int joystickRawY = -1;
  int joystickSwFlag = readJoystickSwitchFlag();

  for (int i = 0; i < 5; i++) {
    long sum = 0;
    for (int k = 0; k < ADC_SAMPLES; k++) {
      sum += analogRead(adcPins[i]);
      delayMicroseconds(150);
    }
    raw[i] = (int)(sum / ADC_SAMPLES);
  }

  if (joystickVrxPin >= 0) {
    long sum = 0;
    for (int k = 0; k < ADC_SAMPLES; k++) {
      sum += analogRead(joystickVrxPin);
      delayMicroseconds(150);
    }
    joystickRawX = (int)(sum / ADC_SAMPLES);
  }

  if (joystickVryPin >= 0) {
    long sum = 0;
    for (int k = 0; k < ADC_SAMPLES; k++) {
      sum += analogRead(joystickVryPin);
      delayMicroseconds(150);
    }
    joystickRawY = (int)(sum / ADC_SAMPLES);
  }

  char packet[160];
  int len = snprintf(
    packet,
    sizeof(packet),
    "OFADC,%lu,%lu,%u,%d,%d,%d,%d,%d,%d,%d,%d,%d\n",
    (unsigned long)adcSeq++,
    (unsigned long)now,
    (unsigned int)adcMask,
    raw[0], raw[1], raw[2], raw[3], raw[4],
    trackingEnabled ? 1 : 0,
    joystickRawX,
    joystickRawY,
    joystickSwFlag
  );

  if (len > 0) {
    udp.beginPacket(hostIp, hostUdpPort);
    udp.write((const uint8_t*)packet, (size_t)len);
    udp.endPacket();
  }
}

static void sendHeartbeat(bool force = false) {
  if (WiFi.status() != WL_CONNECTED || !udpTargetValid) return;

  unsigned long now = millis();
  if (!force && (now - lastHeartbeatMs) < HEARTBEAT_INTERVAL_MS) return;
  lastHeartbeatMs = now;

  String packet = "OFHB " + statusJson() + "\n";
  udp.beginPacket(hostIp, hostUdpPort);
  udp.write((const uint8_t*)packet.c_str(), packet.length());
  udp.endPacket();
}

static String makeDeviceName() {
  uint64_t mac = ESP.getEfuseMac();
  char suffix[8];
  snprintf(suffix, sizeof(suffix), "%04X", (uint16_t)(mac & 0xFFFF));
  return String("openfinger-") + suffix;
}

// -------------------- USB Serial protocol --------------------
//
// Host -> Device:
//   OFHELLO
//   OFSTATUS
//   OFINFO
//   OFVERSION
//   OFIDENT
//   OFRESET
//   OFPROV ssid=...&password=...&save=1&host_ip=192.168.1.2&udp_port=39001&adc_mask=31&role=left
//   OFADC_CFG host_ip=192.168.1.2&udp_port=39001&adc_mask=31&role=left&joystick_vrx_pin=3&joystick_vry_pin=4&joystick_sw_pin=5
//
// Device -> Host:
//   OFSTATUS {json}
//
static void handleSerialLine(String line) {
  line.trim();
  if (line.length() == 0) return;

  if (line == "OFHELLO" || line == "OFSTATUS" || line == "OFINFO" || line == "OFVERSION") {
    printSerialStatus();
    return;
  }

  if (line == "OFIDENT") {
    startIdentifyBlink();
    setStatus("identify", "Blinking onboard LED");
    return;
  }

  if (line == "OFRESET") {
    prefs.begin("openfinger", false);
    prefs.clear();
    prefs.end();
    setStatus("resetting", "Saved config cleared, rebooting");
    delay(500);
    ESP.restart();
    return;
  }

  if (line.startsWith("OFPROV ")) {
    processProvisionPayload(line.substring(7));
    return;
  }

  if (line.startsWith("OFADC_CFG ")) {
    applyAdcConfig(line.substring(10));
    return;
  }

  setStatus("error", "Unknown serial command");
}

static void pollSerial() {
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == '\n' || c == '\r') {
      if (serialLine.length() > 0) {
        handleSerialLine(serialLine);
        serialLine = "";
      }
    } else {
      if (serialLine.length() < 1024) {
        serialLine += c;
      } else {
        serialLine = "";
        setStatus("error", "Serial command too long");
      }
    }
  }
}

static void trySavedWifi() {
  prefs.begin("openfinger", true);
  String ssid = prefs.getString("ssid", "");
  String password = prefs.getString("password", "");
  prefs.end();

  if (ssid.length() > 0 && udpTargetValid) {
    startWifiConnect(ssid, password, false);
  } else {
    setStatus("waiting", "Waiting for USB provisioning");
  }
}

void setup() {
  Serial.begin(115200);
  delay(500);

  deviceName = makeDeviceName();

  ledInit();
  batteryInit();
  trackingSwitchInit();
  joystickSwitchInit();
  trackingEnabledCached = readTrackingEnabled();

  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);

  loadRuntimeConfig();
  batteryInit();
  trackingSwitchInit();
  joystickSwitchInit();
  trackingEnabledCached = readTrackingEnabled();

  WiFi.mode(WIFI_STA);
  WiFi.setSleep(false);

  udp.begin(0);

  Serial.println();
  Serial.printf("OpenFinger %s USB Serial ADC %luFPS %s\n", OPENFINGER_BOARD_TARGET, (unsigned long)adcFps, OPENFINGER_FIRMWARE_VERSION);
  printSerialStatus();

  trySavedWifi();
}

void loop() {
  pollSerial();
  updateIdentifyBlink();

  bool trackingEnabledNow = readTrackingEnabled();
  if (trackingEnabledNow != trackingEnabledCached) {
    trackingEnabledCached = trackingEnabledNow;
    printSerialStatus();
  }

  if (connectRequested) {
    wl_status_t st = WiFi.status();

    if (st == WL_CONNECTED) {
      connectRequested = false;
      ledSet(true);

      if (pendingSave && pendingSSID.length() > 0) {
        prefs.begin("openfinger", false);
        prefs.putString("ssid", pendingSSID);
        prefs.putString("password", pendingPassword);
        prefs.end();
      }

      adcStreaming = udpTargetValid;
      setStatus(adcStreaming ? "connected_streaming" : "connected", "WiFi connected");
      lastAdcMs = 0;
      lastHeartbeatMs = 0;
      sendHeartbeat(true);
    } else if (millis() - connectStartMs > CONNECT_TIMEOUT_MS) {
      connectRequested = false;
      WiFi.disconnect(false, false);
      adcStreaming = false;
      ledSet(false);
      setStatus("error", "WiFi connect timeout or wrong password");
    }
  }

  if (WiFi.status() == WL_CONNECTED) {
    if (!identifyActive) ledSet(true);
    sendHeartbeat();
    sendAdcFrame();
  } else if (!identifyActive) {
    ledSet(false);
  }

  delay(2);
}

