#define TOBOO_PAN_OUTPUT_LOCKED 1
#include "../esp32_single_servo/SingleServoController.h"
#include <assert.h>
#include <iostream>
#include <string>
#include <vector>

struct Output : single_servo::Output {
  bool enabled = false;
  std::vector<std::string> sent;
  bool enable(bool value) override { enabled = value; return true; }
  bool send(const char* frame, size_t length) override {
    assert(enabled && frame[1] != 'A');
    sent.emplace_back(frame, length); return true;
  }
};

int main() {
  assert(!single_servo::validServoFrame("$A090#", 6));
  assert(single_servo::validServoFrame("$B045#", 6));
  Output output;
  single_servo::Controller controller(output);
  controller.start(1, 2);
  auto request = [&](const std::string& verb, int sequence, const std::string& values) {
    char reply[192];
    const std::string line = verb + "," + controller.session() + "," + std::to_string(sequence) + values;
    controller.command(line.c_str(), 0, reply, sizeof(reply));
    return std::string(reply);
  };
  for (const char* verb : {"ARM_S1", "CENTER_S1", "MOVE_S1", "KEEP_S1", "ARM_BOTH", "INIT_BOTH", "MOVE_BOTH", "ARM_FOLLOW", "FOLLOW_BOTH", "HOLD_BOTH", "KEEP_FOLLOW"}) {
    assert(request(verb, 1, "").find("ERR,PAN_OUTPUT_LOCKED,") == 0);
    assert(!output.enabled && output.sent.empty());
  }
  assert(request("ARM_S2", 1, "").find("ACK,") == 0);
  assert(request("CENTER_S2", 2, ",45").find("ACK,") == 0);
  assert(output.sent.size() == 1 && output.sent[0] == "$B045#");
  char reply[192];
  controller.command("DISARM", 1, reply, sizeof(reply));
  assert(!output.enabled && controller.last(0) == -1);
  controller.start(3, 4);
  assert(request("ARM_S1", 1, "").find("ERR,PAN_OUTPUT_LOCKED,") == 0);
  assert(!output.enabled && output.sent.size() == 1);
  std::cout << "PASS: locked pan never enables or emits S1/pair/follow output; isolated S2 remains available\n";
}
