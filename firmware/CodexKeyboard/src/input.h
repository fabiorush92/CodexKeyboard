#pragma once

#include <stdint.h>

void input_setup(uint8_t button1, uint8_t button2, uint8_t button3,
                 uint8_t knob_button, uint8_t encoder_a, uint8_t encoder_b);
void input_update(uint32_t now);
uint8_t input_knob_pressed(void);
