// SPDX-License-Identifier: GPL-3.0-or-later
#ifndef TOBOO_LCD_PROTOCOL_H
#define TOBOO_LCD_PROTOCOL_H
#include <stdint.h>
#include <string.h>

// Fixed storage, strict vocabulary and a per-observation expiry. No actuator output.
class LcdProtocol {
public:
  enum Result { None, Hello, Status, Updated, Rejected };
  uint8_t expression = 7;
  bool expire(uint32_t now) {
    if (expression != 7 && (uint32_t)(now - updatedAt) >= lifetime) {
      expression = 7; return true;
    }
    return false;
  }
  Result feed(char c, uint32_t now) {
    // Once a partial line stalls, discard its suffix through the next newline.
    if ((length || discard) && (uint32_t)(now - lastByte) >= 100) discard = true;
    lastByte = now;
    if (c == '\n') {
      buffer[length] = 0;
      Result result = discard ? Rejected : parse(now);
      length = 0; discard = false; return result;
    }
    if (c == '\r') return None;
    if (!discard && c >= 32 && c <= 126 && length < sizeof(buffer) - 1) buffer[length++] = c;
    else discard = true;
    return None;
  }
  static const char* label(uint8_t i) {
    switch (i) {
      case 0: return "angry"; case 1: return "disgust"; case 2: return "fearful";
      case 3: return "happy"; case 4: return "neutral"; case 5: return "sad";
      case 6: return "surprised"; default: return "unknown";
    }
  }
  static const char* face(uint8_t i) {
    switch (i) {
      case 0: return "(>_<)"; case 1: return "(-_-)"; case 2: return "(O_O)";
      case 3: return "(^_^)"; case 4: return "(._.)"; case 5: return "(T_T)";
      case 6: return "(o_o)"; default: return "(?)";
    }
  }
private:
  char buffer[40] = {};
  uint8_t length = 0;
  bool discard = false, session = false;
  uint32_t lastByte = 0, updatedAt = 0;
  uint16_t lifetime = 0;
  Result parse(uint32_t now) {
    if (strcmp(buffer, "TOBOO_LCD?") == 0) { session = true; expression = 7; return Hello; }
    if (strcmp(buffer, "LCD_STATUS?") == 0) return Status;
    if (!session || strncmp(buffer, "E,", 2) != 0) return Rejected;
    char* comma = strchr(buffer + 2, ',');
    if (!comma) return Rejected;
    *comma = 0;
    uint8_t value = 0;
    while (value < 8 && strcmp(buffer + 2, label(value)) != 0) ++value;
    if (value == 8 || !comma[1]) return Rejected;
    uint16_t ttl = 0;
    for (const char* p = comma + 1; *p; ++p) {
      if (*p < '0' || *p > '9') return Rejected;
      ttl = ttl * 10 + (uint16_t)(*p - '0');
      if (ttl > 500) return Rejected;
    }
    if (ttl == 0) return Rejected;
    expression = value; updatedAt = now; lifetime = ttl; return Updated;
  }
};
#endif
