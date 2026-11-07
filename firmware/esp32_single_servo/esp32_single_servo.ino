#include <Arduino.h>
#include <esp_random.h>
#include <bootloader_random.h>
#include <driver/uart.h>
#include "SingleServoController.h"

#if !CONFIG_IDF_TARGET_ESP32S3 || !ARDUINO_USB_CDC_ON_BOOT || ARDUINO_USB_MODE != 1
#error "Use ESP32-S3 with Hardware CDC and USB CDC On Boot enabled."
#endif

class ServoUart : public single_servo::Output {
 public:
  bool enabled = false;
  bool enable(bool requested) override {
    if (!requested) {
      if (enabled) Serial1.end();
      enabled = false;
      pinMode(17, INPUT); // Avoid driving an unpowered driver board through its RX.
      pinMode(18, INPUT);
      return true;
    }
    if (!enabled) {
      Serial1.begin(9600, SERIAL_8N1, 18, 17);
      enabled = bool(Serial1);
    }
    return enabled;
  }
  bool send(const char* frame, size_t length) override {
    if (!enabled || !single_servo::validServoFrame(frame, length) || Serial1.availableForWrite() < 6) return false;
    if (Serial1.write(reinterpret_cast<const uint8_t*>(frame), length) != length) return false;
    // One 6-byte 8N1 frame takes about 6.25 ms at 9600 baud. Bound the wait so
    // immediate DISARM cannot cut a successfully reported frame in half.
    return uart_wait_tx_done(UART_NUM_1, pdMS_TO_TICKS(20)) == ESP_OK;
  }
};

ServoUart output;
single_servo::Controller controller(output);
single_servo::LineReader reader;
uint32_t lastHeartbeat = 0, droppedReplies = 0;

void sendLine(const char* text) {
  const size_t length = strlen(text);
  if (!Serial || Serial.availableForWrite() < int(length + 1)) { ++droppedReplies; return; }
  Serial.write(reinterpret_cast<const uint8_t*>(text), length);
  Serial.write('\n');
}

void setup() {
  pinMode(17, INPUT);
  pinMode(18, INPUT);
  Serial.setRxBufferSize(512);
  Serial.setTxBufferSize(1024);
  Serial.begin(115200);
  Serial.setTxTimeoutMs(0);
  uint32_t boot[2];
  // No ADC/Wi-Fi/Bluetooth is used. Enable entropy only while making the boot ID.
  bootloader_random_enable();
  esp_fill_random(boot, sizeof(boot));
  bootloader_random_disable();
  controller.start(boot[0], boot[1]);
  // No automatic enable, centering, saved position, sweep or servo UART startup.
}

void loop() {
  uint32_t now = millis();
  controller.tick(now);
  reader.expire(now);
  char response[192];
  for (int budget = 0; budget < 64 && Serial.available(); ++budget) {
    now = millis();
    const auto event = reader.feed(char(Serial.read()), now);
    if (event == single_servo::LineReader::Event::Rejected) {
      sendLine("ERR,FRAMING,S1_S2");
    } else if (event == single_servo::LineReader::Event::Ready) {
      const char* line = reader.line();
      if (!strcmp(line, "INFO")) {
        snprintf(response, sizeof(response),
          "INFO,ESP32-S3,FLASH=%lu,PSRAM=%lu,TX=17,RX=18,BAUD=9600,DROPPED=%lu,S1_S2",
          static_cast<unsigned long>(ESP.getFlashChipSize()),
          static_cast<unsigned long>(ESP.getPsramSize()), static_cast<unsigned long>(droppedReplies));
      } else if (!strncmp(line, "REBOOT_SAFE,", 12)) {
        if (controller.state != single_servo::State::Disarmed || strcmp(line + 12, controller.session())) {
          snprintf(response, sizeof(response), "ERR,REBOOT_STATE_OR_SESSION,S1_S2");
        } else {
          controller.stop(); // Gate is closed before any blocking reset work.
          sendLine("ACK,REBOOT_SAFE,TX_DISABLED");
          delay(30);
          ESP.restart();
          return;
        }
      } else {
        controller.command(line, now, response, sizeof(response));
      }
      sendLine(response);
    }
  }
  // RX18 is reserved, normally disconnected. It never feeds command processing.
  for (int budget = 0; output.enabled && budget < 16 && Serial1.available(); ++budget) Serial1.read();
  now = millis();
  controller.tick(now);
  if (uint32_t(now - lastHeartbeat) >= 1000) {
    lastHeartbeat = now;
    snprintf(response, sizeof(response), "HB,%lu,%s,TX_%s,S1_S2",
      static_cast<unsigned long>(now), controller.stateName(), output.enabled ? "ENABLED" : "DISABLED");
    sendLine(response);
  }
  delay(1);
}
