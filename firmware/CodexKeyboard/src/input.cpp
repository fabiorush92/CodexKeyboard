#include <Arduino.h>

#include "input.h"
#include "led.h"
#include "protocol.h"
#include "util.h"

#define BUTTON_COUNT 4
#define BUTTON_MASK 0x0F
#define KNOB_BUTTON_MASK 0x08
#define DEBOUNCE_MS 8

// Hardware calibration point: the tested encoder accepts three valid
// transitions before returning to its idle state.
#define ENCODER_TRANSITIONS_PER_DETENT 3

static __xdata uint8_t button_pins_s[BUTTON_COUNT];
static __xdata uint8_t debounce_ms_s[BUTTON_COUNT];
static uint8_t stable_buttons_s;
static uint8_t candidate_buttons_s;
static uint8_t encoder_a_s;
static uint8_t encoder_b_s;
static uint8_t encoder_previous_s;
static int8_t encoder_progress_s;
static uint32_t last_button_scan_s;

static uint8_t read_buttons(void)
{
  uint8_t state = 0;
  for (uint8_t i = 0; i < BUTTON_COUNT; i++)
  {
    if (!digitalRead(button_pins_s[i]))
    {
      state |= 1 << i;
    }
  }
  return state;
}

static void update_encoder(void)
{
  uint8_t current = (digitalRead(encoder_a_s) << 1) | digitalRead(encoder_b_s);
  if (current == encoder_previous_s)
  {
    return;
  }

  uint8_t transition = (encoder_previous_s << 2) | current;
  switch (transition)
  {
  case 0x02:
  case 0x04:
  case 0x0B:
  case 0x0D:
    encoder_progress_s++;
    break;
  case 0x01:
  case 0x07:
  case 0x08:
  case 0x0E:
    encoder_progress_s--;
    break;
  default:
    encoder_progress_s = 0;
    break;
  }

  encoder_previous_s = current;
  if (current != 0x03)
  {
    return;
  }

  if (encoder_progress_s >= ENCODER_TRANSITIONS_PER_DETENT)
  {
    protocol_queue_input(CK_CONTROL_ENCODER, CK_INPUT_ROTATE, 1,
                         stable_buttons_s);
  }
  else if (encoder_progress_s <= -ENCODER_TRANSITIONS_PER_DETENT)
  {
    protocol_queue_input(CK_CONTROL_ENCODER, CK_INPUT_ROTATE, -1,
                         stable_buttons_s);
  }
  encoder_progress_s = 0;
}

void input_setup(uint8_t button1, uint8_t button2, uint8_t button3,
                 uint8_t knob_button, uint8_t encoder_a, uint8_t encoder_b)
{
  button_pins_s[0] = button1;
  button_pins_s[1] = button2;
  button_pins_s[2] = button3;
  button_pins_s[3] = knob_button;
  for (uint8_t i = 0; i < BUTTON_COUNT; i++)
  {
    pinMode(button_pins_s[i], INPUT_PULLUP);
    debounce_ms_s[i] = 0;
  }

  encoder_a_s = encoder_a;
  encoder_b_s = encoder_b;
  pinMode(encoder_a_s, INPUT_PULLUP);
  pinMode(encoder_b_s, INPUT_PULLUP);

  stable_buttons_s = read_buttons();
  candidate_buttons_s = stable_buttons_s;
  encoder_previous_s = (digitalRead(encoder_a_s) << 1) |
                       digitalRead(encoder_b_s);
  encoder_progress_s = 0;
  last_button_scan_s = millis();
  protocol_set_button_state(stable_buttons_s);
}

void input_update(uint32_t now)
{
  update_encoder();

  uint32_t elapsed_long = now - last_button_scan_s;
  if (elapsed_long == 0)
  {
    return;
  }
  last_button_scan_s = now;
  uint8_t elapsed = elapsed_long > DEBOUNCE_MS ? DEBOUNCE_MS : elapsed_long;
  uint8_t raw = read_buttons();

  for (uint8_t i = 0; i < BUTTON_COUNT; i++)
  {
    uint8_t mask = 1 << i;
    uint8_t raw_active = raw & mask;
    uint8_t candidate_active = candidate_buttons_s & mask;
    if (!!raw_active != !!candidate_active)
    {
      candidate_buttons_s ^= mask;
      debounce_ms_s[i] = 0;
      continue;
    }

    if (!!raw_active == !!(stable_buttons_s & mask))
    {
      debounce_ms_s[i] = 0;
      continue;
    }

    uint8_t remaining = DEBOUNCE_MS - debounce_ms_s[i];
    debounce_ms_s[i] += elapsed < remaining ? elapsed : remaining;
    if (debounce_ms_s[i] < DEBOUNCE_MS)
    {
      continue;
    }

    stable_buttons_s ^= mask;
    debounce_ms_s[i] = 0;
    protocol_set_button_state(stable_buttons_s);
    protocol_queue_input(i + CK_CONTROL_BUTTON_1,
                         raw_active ? CK_INPUT_PRESS : CK_INPUT_RELEASE, 0,
                         stable_buttons_s);
  }

  if (stable_buttons_s == BUTTON_MASK)
  {
    led_show_bootloader();
    BOOT_now();
  }
}

uint8_t input_knob_pressed(void)
{
  return stable_buttons_s & KNOB_BUTTON_MASK;
}
