#include "../esp32_bench/BenchController.h"
#include <assert.h>
#include <iostream>
#include <string>

using bench::Controller;
using bench::LineReader;
using bench::State;

std::string command(Controller& c, const char* line, uint32_t now) {
  char out[192]; c.command(line, now, out, sizeof(out)); return out;
}
void expect(Controller& c, const char* line, uint32_t now, const char* prefix) {
  const std::string response = command(c, line, now);
  if (response.find(prefix) != 0) {
    std::cerr << line << " -> " << response << " expected " << prefix << '\n';
    assert(false);
  }
}

int main() {
  Controller c;
  assert(c.state == State::Disarmed);
  expect(c, "SET,1,900,900", 0, "ERR,STATE");
  expect(c, "ARM_BENCH,1", 0, "ACK,1,ARM_BENCH");
  expect(c, "SET,2,950,850", 0, "ACK,2,SET");
  for (uint32_t ms = 20; ms <= 100; ms += 20) c.tick(ms);
  assert(c.pan == 905 && c.tilt == 895);
  // Diagnostic traffic and replay must never extend the last accepted command.
  expect(c, "SET,2,900,900", 400, "ERR,SEQUENCE");
  expect(c, "ARM_BENCH,3", 400, "ERR,STATE");
  expect(c, "PING", 499, "PONG");
  assert(c.state == State::Armed);
  c.tick(500);
  assert(c.state == State::TimedOut && c.targetPan == c.pan && c.targetTilt == c.tilt);
  const int held = c.pan;
  c.tick(1000);
  expect(c, "SET,3,950,900", 1000, "ERR,STATE");
  assert(c.pan == held);
  expect(c, "ARM_BENCH,3", 1000, "ACK,3,ARM_BENCH");
  c.tick(1100);
  assert(c.pan == held); // No resumption of the discarded target.
  expect(c, "SET,4,850,950", 1100, "ACK,4,SET");
  expect(c, "DISARM", 1120, "ACK,DISARM");
  assert(c.state == State::Disarmed && c.targetPan == c.pan);
  expect(c, "ARM_BENCH,3", 1200, "ERR,SEQUENCE");

  Controller invalid;
  expect(invalid, "ARM_BENCH,10", 0, "ACK,10");
  const char* bad[] = {"SET,11,849,900", "SET,11,900,951", "SET,11,,900",
    "SET,11,900,900,", "SET,11,-900,900", "SET,11,+900,900",
    "SET,11,900.0,900", "SET,11,900,900x", "SET,4294967296,900,900",
    "SET,11,4294967296,900", "SET,11, 900,900", "ARM_BENCH,", "UNKNOWN"};
  for (const char* line : bad) expect(invalid, line, 400, "ERR,");
  assert(invalid.sequence == 10 && invalid.targetPan == 900);
  invalid.tick(500);
  assert(invalid.state == State::TimedOut);
  expect(invalid, "ARM_BENCH,4294967295", 500, "ACK,4294967295");
  expect(invalid, "SET,0,900,900", 501, "ERR,SEQUENCE");

  Controller gap;
  expect(gap, "ARM_BENCH,0", 100, "ACK,0");
  expect(gap, "SET,1,950,950", 100, "ACK,1");
  gap.tick(400);
  assert(gap.pan == 900); // A scheduling stall must not cause a catch-up jump.
  gap.tick(420);
  assert(gap.pan == 901);
  expect(gap, "SET,2,900,900", 600, "ERR,STATE"); // Boundary cannot revive motion.

  Controller wrap;
  const uint32_t start = UINT32_MAX - 100;
  expect(wrap, "ARM_BENCH,1", start, "ACK,1");
  expect(wrap, "SET,2,950,950", start, "ACK,2");
  for (uint32_t dt = 20; dt <= 480; dt += 20) wrap.tick(start + dt);
  assert(wrap.pan == 924 && wrap.state == State::Armed);
  wrap.tick(start + 499);
  assert(wrap.state == State::Armed);
  wrap.tick(start + 500);
  assert(wrap.state == State::TimedOut);

  LineReader r;
  for (char ch : std::string("STATUS\r")) assert(r.feed(ch, 0) == LineReader::Event::None);
  assert(r.feed('\n', 100) == LineReader::Event::Ready);
  assert(std::string(r.line()) == "STATUS");
  for (int i = 0; i < 200; ++i) r.feed('A', 110);
  assert(r.feed('\n', 111) == LineReader::Event::Rejected);
  r.feed('S', 200); r.expire(400);
  for (char ch : std::string("TATUS")) r.feed(ch, 401);
  assert(r.feed('\n', 401) == LineReader::Event::Rejected);
  r.feed(0, 500);
  assert(r.feed('\n', 500) == LineReader::Event::Rejected);
  for (char ch : std::string("HELLO")) r.feed(ch, 501);
  assert(r.feed('\n', 501) == LineReader::Event::Ready);
  assert(std::string(r.line()) == "HELLO");
  std::cout << "PASS: startup, strict parser, limits, replay, slew, disarm, timeout, rollover, framing\n";
}
