#ifndef TOBOO_MIC_WIRE_H
#define TOBOO_MIC_WIRE_H
#include <stdint.h>
#include <stddef.h>
namespace mic_wire {
constexpr size_t headerSize = 16, payloadSize = 256;
inline uint32_t crc32(const uint8_t* data, size_t size) {
  uint32_t crc = 0xFFFFFFFFu;
  for (size_t i = 0; i < size; ++i) {
    crc ^= data[i];
    for (uint8_t bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ ((crc & 1) ? 0xEDB88320u : 0);
  }
  return ~crc;
}
inline void put32(uint8_t* out, uint32_t value) {
  for (uint8_t i = 0; i < 4; ++i) out[i] = value >> (i * 8);
}
inline size_t frame(uint8_t* out, uint32_t session, uint32_t sequence, uint8_t kind, const uint8_t* pcm, uint16_t size) {
  if (size > payloadSize || size % 2 || kind < 1 || kind > 3 || (kind != 1 && size)) return 0;
  out[0] = 0xA5; out[1] = 0x5A; out[2] = 0xC3; out[3] = 0x3C;
  put32(out + 4, session); put32(out + 8, sequence);
  out[12] = size & 255; out[13] = size >> 8; out[14] = kind; out[15] = 0;
  for (uint16_t i = 0; i < size; ++i) out[16 + i] = pcm[i];
  put32(out + 16 + size, crc32(out, 16 + size));
  return 20 + size;
}
// ICS43432's signed 24-bit sample is left-aligned in a 32-bit I2S slot.
inline int16_t sample(int32_t value) { return int16_t(value >> 16); }
}
#endif
