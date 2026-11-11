// SPDX-License-Identifier: GPL-3.0-or-later
#ifndef TOBOO_LCD_FACE_H
#define TOBOO_LCD_FACE_H
#include "LcdProtocol.h"

namespace LcdFace {
constexpr uint8_t glyphCount = 6;
using Frame = uint8_t[glyphCount][8];

// Each 10×16 eye uses four 5×8 characters. The two eyes share those slots.
inline void frameAt(LcdRole role, uint32_t elapsed, Frame& glyphs) {
  const uint32_t cycle = elapsed % 4400;
  const bool blinks = role == LcdRole::Idle || role == LcdRole::Attentive || role == LcdRole::Responding;
  const bool closed = blinks && cycle >= 3500 && cycle < 3620;
  const bool closing = blinks && cycle >= 3420 && cycle < 3700;
  const bool wide = role == LcdRole::Attentive;
  const uint16_t smile[] = {0x078, 0x0FC, 0x186, 0x303, 0x201};
  const int8_t smileTop = elapsed % 1200 < 600 ? 5 : 6;
  for (uint8_t y = 0; y < 16; ++y) {
    uint16_t pixels = 0;
    if (closed) {
      if (y == 9) pixels = 0x1FE;
    } else if (closing) {
      if (y >= 7 && y <= 10) pixels = y == 7 || y == 10 ? 0x1FE : 0x3FF;
    } else if (role == LcdRole::Happy) {
      if (y >= smileTop && y < smileTop + 5) pixels = smile[y - smileTop];
    } else if (y >= (wide ? 1 : 2) && y < (wide ? 15 : 14)) {
      const uint8_t edge = wide ? 1 : 2;
      if (y == edge || y == 15 - edge) pixels = 0x0FC;
      else if (y == edge + 1 || y == 14 - edge) pixels = 0x1FE;
      else pixels = 0x3FF;
      if (role == LcdRole::Thinking && y >= 5 && y <= 8) {
        const uint16_t pupil = elapsed % 2000 < 1000 ? 0x0E0 : 0x01C;
        pixels &= ~pupil;
      }
    }
    const uint8_t slot = (y / 8) * 2;
    glyphs[slot][y % 8] = pixels >> 5;
    glyphs[slot + 1][y % 8] = pixels & 0x1F;
  }
  const uint8_t mouthPhase = (elapsed / 140) % 4;
  const uint16_t talking[] = {0, 0x078, 0x0CC, 0x084, 0x0CC, 0x078, 0, 0};
  const uint16_t smiling[] = {0, 0, 0x102, 0x102, 0x084, 0x078, 0, 0};
  for (uint8_t y = 0; y < 8; ++y) {
    uint16_t pixels = 0;
    if (role == LcdRole::Happy) pixels = smiling[y];
    else if (role == LcdRole::Responding && mouthPhase == 1) pixels = talking[y];
    else if (role == LcdRole::Responding && mouthPhase == 3 && y >= 3 && y <= 5) pixels = 0x078;
    else if (y == 4) pixels = role == LcdRole::Thinking ? 0x04A : 0x078;
    glyphs[4][y] = pixels >> 5;
    glyphs[5][y] = pixels & 0x1F;
  }
}
}
#endif
