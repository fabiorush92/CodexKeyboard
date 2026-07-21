#pragma once

#include <stdint.h>

#define LED_MAX_COMPONENT 255

void led_setup(void);
void led_set_scene(uint8_t scene, uint8_t effect, uint8_t brightness,
                   uint16_t period_10ms);
void led_set_rgb(const __xdata uint8_t *rgb);
void led_show_absent(void);
void led_show_bootloader(void);
void led_update(uint32_t now);
