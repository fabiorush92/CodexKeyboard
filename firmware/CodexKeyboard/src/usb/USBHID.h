#ifndef __CODEX_KEYBOARD_USB_HID_H__
#define __CODEX_KEYBOARD_USB_HID_H__

// Reports include the HID report ID and use the fixed v1 wire length.

#include <stdint.h>
#include "include/ch5xx.h"

#ifdef __cplusplus
extern "C" {
#endif

void USBInit(void);
uint8_t USB_send_report(const __xdata uint8_t *report);
uint8_t USB_receive_report(__xdata uint8_t *report);
uint8_t USB_claim_report_completion(void);
uint8_t USB_take_transport_reset(void);
uint8_t USB_endpoint_halted(uint8_t endpoint_address);
void USB_set_endpoint_halt(uint8_t endpoint_address);
void USB_clear_endpoint_halt(uint8_t endpoint_address);
void USB_cancel_pending_in(void);
void USB_transport_reset(void);

#ifdef __cplusplus
}
#endif

#endif
