#include "../esp32_single_servo/MicWire.h"
#include <assert.h>
#include <stdio.h>
int main() {
  assert(mic_wire::crc32(reinterpret_cast<const uint8_t*>("123456789"), 9) == 0xCBF43926u);
  assert(mic_wire::sample(INT32_MIN) == -32768);
  assert(mic_wire::sample(INT32_MAX) == 32767);
  assert(mic_wire::sample(0x00120000) == 18);
  uint8_t bytes[276], pcm[256] = {};
  assert(mic_wire::frame(bytes, 42, 0, 1, pcm, 256) == 276);
  assert(bytes[0] == 0xA5 && bytes[4] == 42 && bytes[12] == 0 && bytes[13] == 1);
  assert(mic_wire::frame(bytes, 42, 1, 2, nullptr, 0) == 20);
  assert(mic_wire::frame(bytes, 42, 0, 1, pcm, 257) == 0);
  assert(mic_wire::frame(bytes, 42, 0, 1, pcm, 255) == 0);
  assert(mic_wire::frame(bytes, 42, 0, 2, pcm, 2) == 0);
  puts("PASS: microphone frame limits, end marker, CRC32 and signed I2S conversion.");
}
