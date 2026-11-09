// toboo acquisition only. No servo output. Enable channels only after checking the actual module.
#include <Arduino.h>
#include <Wire.h>

#ifndef TOBOO_ENABLE_MMA
#define TOBOO_ENABLE_MMA 0
#endif
#ifndef TOBOO_ENABLE_ULTRASONIC
#define TOBOO_ENABLE_ULTRASONIC 0
#endif
static const uint8_t TriggerPin = 7, EchoPin = 8;
#if TOBOO_ENABLE_MMA
static uint8_t mmaAddress = 0;
#endif
static uint32_t sequence = 0, lastAcceleration = 0, lastDistance = 0;
static bool streaming = false;
static char input[16];
static uint8_t inputLength = 0;
static bool overflow = false;

static void emit(char channel, bool valid, int x, int y = 0, int z = 0) {
  char body[84];
  snprintf(body, sizeof(body), "T1,%lu,%lu,%c,%d,%d,%d,%d", (unsigned long)sequence++,
    (unsigned long)millis(), channel, valid ? 1 : 0, valid ? x : 0, valid ? y : 0, valid ? z : 0);
  uint8_t checksum = 0;
  for (const char* p = body; *p; ++p) checksum ^= (uint8_t)*p;
  Serial.print(body); Serial.print('*');
  if (checksum < 16) Serial.print('0');
  Serial.println(checksum, HEX);
}

#if TOBOO_ENABLE_MMA
static bool readRegisters(uint8_t reg, uint8_t* data, uint8_t count) {
  Wire.clearWireTimeoutFlag();
  Wire.beginTransmission(mmaAddress); Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom(mmaAddress, count) != count || Wire.getWireTimeoutFlag()) return false;
  for (uint8_t i = 0; i < count; ++i) data[i] = Wire.read();
  return true;
}
static bool writeRegister(uint8_t reg, uint8_t value) {
  Wire.clearWireTimeoutFlag();
  Wire.beginTransmission(mmaAddress); Wire.write(reg); Wire.write(value);
  return Wire.endTransmission() == 0 && !Wire.getWireTimeoutFlag();
}
static void initializeMma() {
  // Requires verified 3.3V-compatible module/bidirectional level shifting and pullups.
  Wire.begin(); Wire.setWireTimeout(3000, true);
  for (uint8_t address = 0x1c; address <= 0x1d; ++address) {
    mmaAddress = address;
    uint8_t identity;
    if (readRegisters(0x0d, &identity, 1) && identity == 0x2a &&
        writeRegister(0x2a, 0x20) && writeRegister(0x0e, 0x00) && writeRegister(0x2a, 0x21)) return;
  }
  mmaAddress = 0;
}
static void acceleration() {
  uint8_t data[7];
  // STATUS ZYXDR is required: do not publish a repeated register value as a new observation.
  if (!mmaAddress || !readRegisters(0x00, data, 7) || !(data[0] & 0x08)) { emit('A', false, 0); return; }
  int values[3];
  for (uint8_t i = 0; i < 3; ++i) {
    int16_t raw = (int16_t)(((uint16_t)data[1 + 2*i] << 8) | data[2 + 2*i]);
    // +/-2g: left-justified signed 12-bit counts, 1024 counts/g. Use 32-bit arithmetic on AVR.
    values[i] = (int)((int32_t)(raw / 16) * 1000L / 1024L);
  }
  emit('A', true, values[0], values[1], values[2]);
}
#endif

void setup() {
  Serial.begin(115200);
#if TOBOO_ENABLE_ULTRASONIC
  pinMode(TriggerPin, OUTPUT); digitalWrite(TriggerPin, LOW); pinMode(EchoPin, INPUT);
#endif
#if TOBOO_ENABLE_MMA
  initializeMma();
#endif
}

void loop() {
  // Fixed buffer; the only accepted host request starts a fresh telemetry session.
  while (Serial.available()) {
    const char c = (char)Serial.read();
    if (c == '\n') {
      input[inputLength] = 0;
      if (!overflow && strcmp(input, "TOBOO?") == 0) {
        sequence = 0; streaming = true; Serial.println(F("TOBOO_SENSOR,1,UNO"));
      }
      inputLength = 0; overflow = false;
    } else if (c != '\r') {
      if (inputLength < sizeof(input) - 1) input[inputLength++] = c;
      else overflow = true;
    }
  }
  if (!streaming) return;
  const uint32_t now = millis();
  if ((uint32_t)(now - lastAcceleration) >= 50) {
    lastAcceleration = now;
#if TOBOO_ENABLE_MMA
    acceleration();
#else
    emit('A', false, 0);
#endif
  }
  if ((uint32_t)(now - lastDistance) >= 100) {
    lastDistance = now;
#if TOBOO_ENABLE_ULTRASONIC
    digitalWrite(TriggerPin, LOW); delayMicroseconds(2);
    digitalWrite(TriggerPin, HIGH); delayMicroseconds(10); digitalWrite(TriggerPin, LOW);
    // Bounded blocking: at most 25ms. No echo is invalid, never an infinite/free-space distance.
    const unsigned long duration = pulseIn(EchoPin, HIGH, 25000UL);
    const unsigned long mm = duration * 343UL / 2000UL; // nominal speed of sound, uncalibrated
    emit('D', duration != 0 && mm >= 20 && mm <= 4000, (int)mm);
#else
    emit('D', false, 0);
#endif
  }
}
