#include <Arduino.h>

#include "led.h"
#include "neo/neo.h"
#include "protocol.h"

#define LED_COUNT 3
#define ANIMATION_FRAME_MS 20

static uint8_t scene_s;
static uint8_t effect_s;
static uint8_t brightness_s;
static uint16_t period_ms_s;
static uint8_t direct_s;
static uint8_t dirty_s;
static __xdata uint8_t rgb_s[LED_COUNT * 3];
static uint32_t last_frame_s;

static uint8_t scaled(uint8_t ratio, uint8_t level)
{
  return ((uint16_t)ratio * level) >> 2;
}

static void write_pixel(uint8_t pixel, uint8_t red, uint8_t green,
                        uint8_t blue, uint8_t level)
{
  NEO_writeColor(pixel, scaled(red, level), scaled(green, level),
                 scaled(blue, level));
}

static void render_scene(uint8_t level)
{
  for (uint8_t i = 0; i < LED_COUNT; i++)
  {
    NEO_writeColor(i, 0, 0, 0);
  }

  switch (scene_s)
  {
  case CK_SCENE_COMPANION_ABSENT:
    write_pixel(0, 4, 1, 0, level);
    write_pixel(1, 4, 1, 0, level);
    write_pixel(2, 4, 1, 0, level);
    break;
  case CK_SCENE_COMPANION_CONNECTED:
    write_pixel(0, 0, 3, 4, level);
    write_pixel(1, 0, 3, 4, level);
    write_pixel(2, 0, 3, 4, level);
    break;
  case CK_SCENE_CODEX_UNAVAILABLE:
  case CK_SCENE_ACTION_FAILED:
    write_pixel(0, 4, 0, 0, level);
    write_pixel(1, 4, 0, 0, level);
    write_pixel(2, 4, 0, 0, level);
    break;
  case CK_SCENE_EFFORT_MEDIUM:
    write_pixel(0, 0, 2, 4, level);
    break;
  case CK_SCENE_EFFORT_HIGH:
    write_pixel(0, 4, 2, 0, level);
    write_pixel(1, 4, 2, 0, level);
    break;
  case CK_SCENE_EFFORT_ULTRA:
    write_pixel(0, 4, 0, 4, level);
    write_pixel(1, 4, 0, 4, level);
    write_pixel(2, 4, 0, 4, level);
    break;
  case CK_SCENE_ACTION_SUCCEEDED:
  case CK_SCENE_COMPLETED:
    write_pixel(0, 0, 4, 0, level);
    write_pixel(1, 0, 4, 0, level);
    write_pixel(2, 0, 4, 0, level);
    break;
  case CK_SCENE_TURN_ACTIVE:
    write_pixel(0, 0, 0, 4, level);
    write_pixel(1, 0, 0, 4, level);
    write_pixel(2, 0, 0, 4, level);
    break;
  case CK_SCENE_WAITING_FOR_APPROVAL:
    write_pixel(0, 4, 3, 0, level);
    write_pixel(1, 4, 3, 0, level);
    write_pixel(2, 4, 3, 0, level);
    break;
  }
}

void led_setup(void)
{
  last_frame_s = 0;
  led_show_absent();
}

void led_set_scene(uint8_t scene, uint8_t effect, uint8_t brightness,
                   uint16_t period_10ms)
{
  scene_s = scene;
  effect_s = effect;
  brightness_s = brightness;
  period_ms_s = period_10ms * 10;
  direct_s = 0;
  dirty_s = 1;
}

void led_set_rgb(const __xdata uint8_t *rgb)
{
  for (uint8_t i = 0; i < LED_COUNT * 3; i++)
  {
    rgb_s[i] = rgb[i];
  }
  direct_s = 1;
  dirty_s = 1;
}

void led_show_absent(void)
{
  led_set_scene(CK_SCENE_COMPANION_ABSENT, CK_EFFECT_BREATHE, 8, 200);
}

void led_show_bootloader(void)
{
  const uint16_t half = LED_BOOTLOADER_TRANSITION_MS >> 1;
  for (uint16_t elapsed = 0; elapsed < LED_BOOTLOADER_TRANSITION_MS;
       elapsed += ANIMATION_FRAME_MS)
  {
    uint16_t triangle = elapsed < half ? elapsed : LED_BOOTLOADER_TRANSITION_MS - elapsed;
    uint8_t blue = ((uint32_t)LED_MAX_COMPONENT * triangle) / half;
    for (uint8_t i = 0; i < LED_COUNT; i++)
    {
      NEO_writeColor(i, 0, 0, blue);
    }
    NEO_update();
    delay(ANIMATION_FRAME_MS);
  }

  for (uint8_t i = 0; i < LED_COUNT; i++)
  {
    NEO_writeColor(i, 0, 0, LED_MAX_COMPONENT);
  }
  NEO_update();
}

void led_update(uint32_t now)
{
  if (direct_s)
  {
    if (!dirty_s)
    {
      return;
    }
    for (uint8_t i = 0; i < LED_COUNT; i++)
    {
      NEO_writeColor(i, rgb_s[i * 3], rgb_s[i * 3 + 1], rgb_s[i * 3 + 2]);
    }
  }
  else
  {
    if (!dirty_s && effect_s == CK_EFFECT_SOLID)
    {
      return;
    }
    if (!dirty_s && now - last_frame_s < ANIMATION_FRAME_MS)
    {
      return;
    }

    uint8_t level = brightness_s;
    if (effect_s == CK_EFFECT_BLINK)
    {
      level = (now % period_ms_s) < (period_ms_s >> 1) ? brightness_s : 0;
    }
    else if (effect_s == CK_EFFECT_BREATHE)
    {
      uint16_t phase = now % period_ms_s;
      uint16_t half = period_ms_s >> 1;
      uint16_t triangle = phase < half ? phase : period_ms_s - phase;
      level = ((uint32_t)brightness_s * triangle) / half;
    }
    render_scene(level);
  }

  NEO_update();
  dirty_s = 0;
  last_frame_s = now;
}
