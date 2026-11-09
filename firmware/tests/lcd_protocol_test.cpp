#include "../uno_lcd/LcdProtocol.h"
#include <assert.h>
#include <stdio.h>

LcdProtocol::Result send(LcdProtocol& p, const char* line, uint32_t now) {
  LcdProtocol::Result result = LcdProtocol::None;
  for (; *line; ++line) result = p.feed(*line, now);
  return result;
}
int main() {
  LcdProtocol p;
  assert(p.expression == 7);
  assert(send(p, "E,happy,400\n", 0) == LcdProtocol::Rejected);
  assert(send(p, "TOBOO_LCD?\r\n", 1) == LcdProtocol::Hello);
  for (uint8_t i = 0; i < 8; ++i) {
    char line[40]; snprintf(line, sizeof(line), "E,%s,400\n", LcdProtocol::label(i));
    assert(send(p, line, 2) == LcdProtocol::Updated);
    assert(p.expression == i);
  }
  assert(send(p, "E,happy,400\n", 100) == LcdProtocol::Updated);
  const char* invalid[] = {"E,happy,501\n", "E,happy,0\n", "E,happy,-1\n", "E,happy,12x\n",
    "E,happy,20,0\n", "E,Happy,100\n", "E,happy,\n", "MOVE,180\n",
    "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxE,sad,100\n"};
  for (const char* text : invalid) assert(send(p, text, 200) == LcdProtocol::Rejected);
  assert(!p.expire(499)); assert(p.expire(500)); assert(p.expression == 7);
  send(p, "E,hap", 600);
  assert(send(p, "py,400\n", 701) == LcdProtocol::Rejected);
  assert(send(p, "E,sad,200\n", 702) == LcdProtocol::Updated);
  assert(p.expression == 5);
  assert(send(p, "TOBOO_LCD?\n", 703) == LcdProtocol::Hello); assert(p.expression == 7);
  send(p, "E,happy,200\n", UINT32_MAX - 100);
  assert(!p.expire(98)); assert(p.expire(99));
  puts("PASS: LCD boot, handshake, labels, strict parsing, stalled/oversized input, expiry and millis wrap.");
}
