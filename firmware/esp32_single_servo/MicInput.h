#ifndef TOBOO_MIC_INPUT_H
#define TOBOO_MIC_INPUT_H
#include <ESP_I2S.h>
#include <atomic>
#include "MicWire.h"

class MicInput {
  struct Block { uint32_t session; uint8_t pcm[mic_wire::payloadSize]; };
  I2SClass i2s;
  QueueHandle_t blocks = nullptr;
  std::atomic<bool> recording{false}, failed{false};
  std::atomic<uint32_t> session{0};
  bool ready = false, finishing = false;
  uint32_t sequence = 0, started = 0;
  static void run(void* self) { static_cast<MicInput*>(self)->capture(); }
  void capture() {
    int32_t samples[256];
    for (;;) {
      const uint32_t id = session.load();
      const size_t count = i2s.readBytes(reinterpret_cast<char*>(samples), sizeof(samples));
      if (!recording.load() || id != session.load()) continue;
      if (count != sizeof(samples)) { failed.store(true); recording.store(false); continue; }
      Block block; block.session = id;
      for (size_t i = 0; i < 128; ++i) {
        const int16_t value = mic_wire::sample(samples[i * 2]); // LR tied to GND selects left.
        block.pcm[i * 2] = uint16_t(value) & 255;
        block.pcm[i * 2 + 1] = uint16_t(value) >> 8;
      }
      if (recording.load() && id == session.load() && xQueueSend(blocks, &block, 0) != pdTRUE) {
        failed.store(true); recording.store(false);
      }
    }
  }
  bool send(uint8_t kind, const uint8_t* data = nullptr, uint16_t size = 0) {
    uint8_t bytes[20 + mic_wire::payloadSize];
    const size_t length = mic_wire::frame(bytes, session.load(), sequence, kind, data, size);
    if (!Serial || Serial.availableForWrite() < int(length)) return false;
    if (Serial.write(bytes, length) != length) return false;
    ++sequence; return true;
  }
 public:
  void begin() {
    i2s.setPins(4, 5, -1, 6);
    ready = i2s.begin(I2S_MODE_STD, 16000, I2S_DATA_BIT_WIDTH_32BIT, I2S_SLOT_MODE_STEREO);
    if (!ready) return;
    blocks = xQueueCreate(8, sizeof(Block));
    ready = blocks && xTaskCreatePinnedToCore(run, "toboo-mic", 4096, this, 1, nullptr, 0) == pdPASS;
  }
  bool command(const char* line) {
    if (!strcmp(line, "TOBOO_MIC?")) {
      Serial.println(ready ? "TOBOO_MIC,1,ICS43432,16000,16,1,4,5,6,LEFT" : "MIC_ERROR,I2S_INIT");
      return true;
    }
    const bool start = !strncmp(line, "MIC_START,", 10);
    if (!start && strncmp(line, "MIC_STOP,", 9)) return false;
    const char* value = line + (start ? 10 : 9);
    if (strlen(value) != 8) return true;
    for (uint8_t i = 0; i < 8; ++i) if (!isxdigit(value[i])) return true;
    const uint32_t id = strtoul(value, nullptr, 16);
    if (!id) return true;
    if (start) {
      if (!ready || session.load()) { Serial.println("MIC_ERROR,NOT_READY_OR_BUSY"); return true; }
      xQueueReset(blocks); failed.store(false); sequence = 0; finishing = false;
      session.store(id); started = millis();
      Serial.printf("MIC_BEGIN,%08lx\n", static_cast<unsigned long>(id));
      recording.store(true);
    } else if (session.load() == id && id) { recording.store(false); finishing = true; }
    return true;
  }
  void tick() {
    if (!session.load()) return;
    if (!Serial) { recording.store(false); session.store(0); return; }
    if (uint32_t(millis() - started) >= 30000) { recording.store(false); finishing = true; }
    if (failed.load()) {
      if (send(3)) { recording.store(false); session.store(0); }
      return;
    }
    Block block;
    if (xQueueReceive(blocks, &block, 0) == pdTRUE) {
      if (block.session == session.load() && !send(1, block.pcm, sizeof(block.pcm))) {
        recording.store(false); failed.store(true);
      }
    } else if (finishing && send(2)) { session.store(0); finishing = false; }
  }
};
#endif
