#include <Arduino.h>
#include "BenchController.h"

#if !CONFIG_IDF_TARGET_ESP32S3 || !ARDUINO_USB_CDC_ON_BOOT || ARDUINO_USB_MODE != 1
#error "Requires ESP32-S3, Hardware CDC and JTAG, USB CDC On Boot enabled."
#endif

// Intentionally no Serial1, GPIO17/18, PWM, servo library or driver protocol.
// No build option enables motors in this sketch.
bench::Controller controller;
bench::LineReader reader;
uint32_t lastHeartbeat = 0;
bool psramCheck = false;
uint32_t droppedReplies = 0;

void sendLine(const char* text) {
  const size_t length = strlen(text);
  if (!Serial || Serial.availableForWrite() < int(length + 1)) {
    ++droppedReplies;
    return; // Host backpressure must not block the watchdog.
  }
  Serial.write(reinterpret_cast<const uint8_t*>(text), length);
  Serial.write('\n');
}

bool checkPsram() {
  const size_t count = 16384; // 64 KiB sample, NOT a full memory qualification
  volatile uint32_t* sample = static_cast<uint32_t*>(ps_malloc(count * sizeof(uint32_t)));
  if (!sample) return false;
  bool ok = true;
  for (size_t i = 0; i < count; ++i) sample[i] = uint32_t(i) ^ 0xa55a3cc3u;
  for (size_t i = 0; i < count; ++i) {
    if (sample[i] != (uint32_t(i) ^ 0xa55a3cc3u)) { ok = false; break; }
  }
  free(const_cast<uint32_t*>(sample));
  return ok;
}

void setup() {
  Serial.setRxBufferSize(512);
  Serial.setTxBufferSize(1024);
  Serial.begin(115200);
  Serial.setTxTimeoutMs(0);
  psramCheck = psramFound() && checkPsram();
  // Never wait for the PC to open the port. Startup remains DISARMED.
}

void loop() {
  uint32_t now = millis();
  controller.tick(now);
  reader.expire(now);
  char response[192];
  // Bounded receive work prevents malformed input flooding from starving tick().
  for (int budget = 0; budget < 64 && Serial.available(); ++budget) {
    now = millis();
    const auto event = reader.feed(char(Serial.read()), now);
    if (event == bench::LineReader::Event::Rejected) {
      sendLine("ERR,FRAMING,OUTPUT_DISABLED");
    } else if (event == bench::LineReader::Event::Ready) {
      if (strcmp(reader.line(), "INFO") == 0) {
        snprintf(response, sizeof(response),
          "INFO,ESP32-S3,FLASH=%lu,PSRAM=%lu,PSRAM_SAMPLE=%s,DROPPED=%lu,OUTPUT_DISABLED",
          static_cast<unsigned long>(ESP.getFlashChipSize()),
          static_cast<unsigned long>(ESP.getPsramSize()), psramCheck ? "PASS" : "FAIL",
          static_cast<unsigned long>(droppedReplies));
      } else {
        controller.command(reader.line(), now, response, sizeof(response));
      }
      sendLine(response);
    }
  }
  now = millis();
  controller.tick(now);
  if (uint32_t(now - lastHeartbeat) >= 1000) {
    lastHeartbeat = now;
    snprintf(response, sizeof(response), "HB,%lu,%s,OUTPUT_DISABLED",
             static_cast<unsigned long>(now), controller.stateName());
    sendLine(response);
  }
  delay(1);
}
