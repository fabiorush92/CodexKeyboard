/* Created by Deqing Sun for use with CH55xduino. */

#include "USBhandler.h"

#include "USBconstant.h"
#include "USBHID.h"

// clang-format off
__xdata __at (EP0_ADDR) uint8_t Ep0Buffer[8];
__xdata __at (EP1_ADDR) uint8_t Ep1Buffer[128];       //on page 47 of data sheet, the receive buffer need to be min(possible packet size+2,64), IN and OUT buffer, must be even address
// clang-format on

#if (EP1_ADDR + 128) > USER_USB_RAM
#error "This example needs more USB ram. Increase this setting in menu."
#endif

__data uint16_t SetupLen;
__data uint8_t SetupReq;
volatile __xdata uint8_t UsbConfig;

__code uint8_t *__data pDescr;

static __data uint8_t SerialDescriptorActive;
static __data uint8_t DescriptorOffset;

volatile uint8_t usbMsgFlags = 0; // uint8_t usbMsgFlags copied from VUSB

inline void NOP_Process(void) {}

static uint8_t uid_byte(uint8_t index)
{
  uint16_t address;
  switch (index)
  {
  case 0:
    address = ROM_CHIP_ID_HX;
    break;
  case 1:
    address = ROM_CHIP_ID_LO;
    break;
  case 2:
    address = ROM_CHIP_ID_LO + 1;
    break;
  case 3:
    address = ROM_CHIP_ID_HI;
    break;
  default:
    address = ROM_CHIP_ID_HI + 1;
    break;
  }
  return *((__code uint8_t *)address);
}

static uint8_t hex_digit(uint8_t value)
{
  value &= 0x0F;
  return value < 10 ? '0' + value : 'A' + value - 10;
}

static uint8_t serial_descriptor_byte(uint8_t index)
{
  if (index == 0)
  {
    return 26;
  }
  if (index == 1)
  {
    return DTYPE_String;
  }
  if (index & 1)
  {
    return 0;
  }

  uint8_t character = (index - 2) >> 1;
  if (character == 0)
  {
    return 'C';
  }
  if (character == 1)
  {
    return 'K';
  }

  character -= 2;
  uint8_t value = uid_byte(character >> 1);
  return hex_digit(character & 1 ? value : value >> 4);
}

void USB_EP0_SETUP() {
  __data uint8_t len = USB_RX_LEN;
  if (len == (sizeof(USB_SETUP_REQ))) {
    SetupLen = ((uint16_t)UsbSetupBuf->wLengthH << 8) | (UsbSetupBuf->wLengthL);
    len = 0; // Default is success and upload 0 length
    SetupReq = UsbSetupBuf->bRequest;
    usbMsgFlags = 0;
    SerialDescriptorActive = 0;
    DescriptorOffset = 0;
    if ((UsbSetupBuf->bRequestType & USB_REQ_TYP_MASK) !=
        USB_REQ_TYP_STANDARD) // Not standard request
    {

      // here is the commnunication starts, refer to usbFunctionSetup of USBtiny
      // or usb_setup in usbtiny

      switch ((UsbSetupBuf->bRequestType & USB_REQ_TYP_MASK)) {
      case USB_REQ_TYP_VENDOR: {
        switch (SetupReq) {
        default:
          len = 0xFF; // command not supported
          break;
        }
        break;
      }
      case USB_REQ_TYP_CLASS: {
        switch (SetupReq) {
        default:
          len = 0xFF; // command not supported
          break;
        }
        break;
      }
      default:
        len = 0xFF; // command not supported
        break;
      }

    } else // Standard request
    {
      switch (SetupReq) // Request ccfType
      {
      case USB_GET_DESCRIPTOR:
        switch (UsbSetupBuf->wValueH) {
        case 1: // Device Descriptor
          pDescr = (__code uint8_t *)
              DeviceDescriptor; // Put Device Descriptor into outgoing buffer
          len = sizeof(USB_Descriptor_Device_t);
          break;
        case 2: // Configure Descriptor
          pDescr = (__code uint8_t *)ConfigurationDescriptor;
          len = sizeof(USB_Descriptor_Configuration_t);
          break;
        case 3:
          if (UsbSetupBuf->wValueL == 0) {
            pDescr = LanguageDescriptor;
          } else if (UsbSetupBuf->wValueL == 1 ||
                     UsbSetupBuf->wValueL == 2) {
            pDescr = (__code uint8_t *)ProductDescriptor;
          } else if (UsbSetupBuf->wValueL == 3) {
            SerialDescriptorActive = 1;
          } else {
            len = 0xff;
            break;
          }
          len = SerialDescriptorActive ? 26 : pDescr[0];
          break;
        case 0x22:
          if (UsbSetupBuf->wValueL == 0) {
            pDescr = (__code uint8_t *)ReportDescriptor;
            len = ConfigurationDescriptor.HID_Descriptor.HIDReportLength;
          } else {
            len = 0xff;
          }
          break;
        default:
          len = 0xff; // Unsupported descriptors or error
          break;
        }
        if (len != 0xff) {
          if (SetupLen > len) {
            SetupLen = len; // Limit length
          }
          len = SetupLen >= DEFAULT_ENDP0_SIZE
                    ? DEFAULT_ENDP0_SIZE
                    : SetupLen; // transmit length for this packet
          for (__data uint8_t i = 0; i < len; i++) {
            Ep0Buffer[i] = SerialDescriptorActive
                               ? serial_descriptor_byte(i)
                               : pDescr[i];
          }
          SetupLen -= len;
          if (SerialDescriptorActive) {
            DescriptorOffset = len;
          } else {
            pDescr += len;
          }
        }
        break;
      case USB_SET_ADDRESS:
        SetupLen = UsbSetupBuf->wValueL; // Save the assigned address
        break;
      case USB_GET_CONFIGURATION:
        Ep0Buffer[0] = UsbConfig;
        if (SetupLen >= 1) {
          len = 1;
        }
        break;
      case USB_SET_CONFIGURATION:
        if (UsbSetupBuf->bRequestType != 0x00 || UsbSetupBuf->wValueH ||
            UsbSetupBuf->wValueL > 1 || UsbSetupBuf->wIndexH ||
            UsbSetupBuf->wIndexL || SetupLen) {
          len = 0xFF;
          break;
        }
        UsbConfig = UsbSetupBuf->wValueL;
        USB_transport_reset();
        break;
      case USB_GET_INTERFACE:
        break;
      case USB_SET_INTERFACE:
        break;
      case USB_CLEAR_FEATURE: // Clear Feature
        if (UsbSetupBuf->bRequestType != 0x02 || UsbSetupBuf->wValueH ||
            UsbSetupBuf->wValueL || UsbSetupBuf->wIndexH || SetupLen ||
            UsbConfig != 1 ||
            (UsbSetupBuf->wIndexL != 0x81 && UsbSetupBuf->wIndexL != 0x01)) {
          len = 0xFF;
          break;
        }
        USB_clear_endpoint_halt(UsbSetupBuf->wIndexL);
        break;
      case USB_SET_FEATURE: // Set Feature
        if (UsbSetupBuf->bRequestType != 0x02 || UsbSetupBuf->wValueH ||
            UsbSetupBuf->wValueL || UsbSetupBuf->wIndexH || SetupLen ||
            UsbConfig != 1 ||
            (UsbSetupBuf->wIndexL != 0x81 && UsbSetupBuf->wIndexL != 0x01)) {
          len = 0xFF;
          break;
        }
        USB_set_endpoint_halt(UsbSetupBuf->wIndexL);
        break;
      case USB_GET_STATUS:
        if (UsbSetupBuf->wValueH || UsbSetupBuf->wValueL || SetupLen != 2) {
          len = 0xFF;
          break;
        }
        Ep0Buffer[0] = 0x00;
        Ep0Buffer[1] = 0x00;
        if (UsbSetupBuf->bRequestType == 0x80) {
          if (UsbSetupBuf->wIndexH || UsbSetupBuf->wIndexL) {
            len = 0xFF;
            break;
          }
        } else if (UsbSetupBuf->bRequestType == 0x81) {
          if (UsbConfig != 1 || UsbSetupBuf->wIndexH ||
              UsbSetupBuf->wIndexL) {
            len = 0xFF;
            break;
          }
        } else if (UsbSetupBuf->bRequestType == 0x82) {
          uint16_t endpoint = ((uint16_t)UsbSetupBuf->wIndexH << 8) |
                              UsbSetupBuf->wIndexL;
          if (endpoint == 0x81 || endpoint == 0x01) {
            if (UsbConfig != 1) {
              len = 0xFF;
              break;
            }
            Ep0Buffer[0] = USB_endpoint_halted((uint8_t)endpoint);
          } else if (endpoint != 0x80 && endpoint != 0x00) {
            len = 0xFF;
            break;
          }
        } else {
          len = 0xFF;
          break;
        }
        len = 2;
        break;
      default:
        len = 0xff; // Failed
        break;
      }
    }
  } else {
    len = 0xff; // Wrong packet length
  }
  if (len == 0xff) {
    SetupReq = 0xFF;
    UEP0_CTRL =
        bUEP_R_TOG | bUEP_T_TOG | UEP_R_RES_STALL | UEP_T_RES_STALL; // STALL
  } else if (len <=
             DEFAULT_ENDP0_SIZE) // Tx data to host or send 0-length packet
  {
    UEP0_T_LEN = len;
    UEP0_CTRL = bUEP_R_TOG | bUEP_T_TOG | UEP_R_RES_ACK |
                UEP_T_RES_ACK; // Expect DATA1, Answer ACK
  } else {
    UEP0_T_LEN = 0; // Tx data to host or send 0-length packet
    UEP0_CTRL = bUEP_R_TOG | bUEP_T_TOG | UEP_R_RES_ACK |
                UEP_T_RES_ACK; // Expect DATA1, Answer ACK
  }
}

void USB_EP0_IN() {
  switch (SetupReq) {
  case USB_GET_DESCRIPTOR: {
    __data uint8_t len = SetupLen >= DEFAULT_ENDP0_SIZE
                             ? DEFAULT_ENDP0_SIZE
                             : SetupLen; // send length
    for (__data uint8_t i = 0; i < len; i++) {
      Ep0Buffer[i] = SerialDescriptorActive
                         ? serial_descriptor_byte(DescriptorOffset + i)
                         : pDescr[i];
    }
    // memcpy( Ep0Buffer, pDescr, len );
    SetupLen -= len;
    if (SerialDescriptorActive) {
      DescriptorOffset += len;
    } else {
      pDescr += len;
    }
    UEP0_T_LEN = len;
    UEP0_CTRL ^= bUEP_T_TOG; // Switch between DATA0 and DATA1
  } break;
  case USB_SET_ADDRESS:
    USB_DEV_AD = USB_DEV_AD & bUDA_GP_BIT | SetupLen;
    UEP0_CTRL = UEP_R_RES_ACK | UEP_T_RES_NAK;
    break;
  default:
    UEP0_T_LEN = 0; // End of transaction
    UEP0_CTRL = UEP_R_RES_ACK | UEP_T_RES_NAK;
    break;
  }
}

void USB_EP0_OUT() {
  {
    UEP0_T_LEN = 0;
    UEP0_CTRL |= UEP_R_RES_ACK | UEP_T_RES_NAK; // Respond Nak
  }
}

#pragma save
#pragma nooverlay
void USBInterrupt(void) { // inline not really working in multiple files in SDCC
  if (UIF_TRANSFER) {
    // Dispatch to service functions
    __data uint8_t callIndex = USB_INT_ST & MASK_UIS_ENDP;
    switch (USB_INT_ST & MASK_UIS_TOKEN) {
    case UIS_TOKEN_OUT: { // SDCC will take IRAM if array of function pointer is
                          // used.
      switch (callIndex) {
      case 0:
        EP0_OUT_Callback();
        break;
      case 1:
        EP1_OUT_Callback();
        break;
      case 2:
        EP2_OUT_Callback();
        break;
      case 3:
        EP3_OUT_Callback();
        break;
      case 4:
        EP4_OUT_Callback();
        break;
      default:
        break;
      }
    } break;
    case UIS_TOKEN_SOF: { // SDCC will take IRAM if array of function pointer is
                          // used.
      switch (callIndex) {
      case 0:
        EP0_SOF_Callback();
        break;
      case 1:
        EP1_SOF_Callback();
        break;
      case 2:
        EP2_SOF_Callback();
        break;
      case 3:
        EP3_SOF_Callback();
        break;
      case 4:
        EP4_SOF_Callback();
        break;
      default:
        break;
      }
    } break;
    case UIS_TOKEN_IN: { // SDCC will take IRAM if array of function pointer is
                         // used.
      switch (callIndex) {
      case 0:
        EP0_IN_Callback();
        break;
      case 1:
        EP1_IN_Callback();
        break;
      case 2:
        EP2_IN_Callback();
        break;
      case 3:
        EP3_IN_Callback();
        break;
      case 4:
        EP4_IN_Callback();
        break;
      default:
        break;
      }
    } break;
    case UIS_TOKEN_SETUP: { // SDCC will take IRAM if array of function pointer
                            // is used.
      switch (callIndex) {
      case 0:
        EP0_SETUP_Callback();
        break;
      case 1:
        EP1_SETUP_Callback();
        break;
      case 2:
        EP2_SETUP_Callback();
        break;
      case 3:
        EP3_SETUP_Callback();
        break;
      case 4:
        EP4_SETUP_Callback();
        break;
      default:
        break;
      }
    } break;
    }

    UIF_TRANSFER = 0; // Clear interrupt flag
  }

  // Device mode USB bus reset
  if (UIF_BUS_RST) {
    UEP0_CTRL = UEP_R_RES_ACK | UEP_T_RES_NAK;
    UEP1_CTRL = bUEP_AUTO_TOG | UEP_T_RES_NAK | UEP_R_RES_NAK;

    USB_DEV_AD = 0x00;
    UIF_SUSPEND = 0;
    UIF_TRANSFER = 0;
    UIF_BUS_RST = 0;

    UsbConfig = 0;
    USB_transport_reset();

    // Clear interrupt flag
  }

  // USB bus suspend / wake up
  if (UIF_SUSPEND) {
    UIF_SUSPEND = 0;
    if (USB_MIS_ST & bUMS_SUSPEND) { // Suspend

      // while ( XBUS_AUX & bUART0_TX );                    // Wait for Tx
      // SAFE_MOD = 0x55;
      // SAFE_MOD = 0xAA;
      // WAKE_CTRL = bWAK_BY_USB | bWAK_RXD0_LO;    // Wake up by USB or RxD0
      // PCON |= PD; // Chip sleep SAFE_MOD = 0x55; SAFE_MOD = 0xAA; WAKE_CTRL =
      // 0x00;

    } else {             // Unexpected interrupt, not supposed to happen !
      USB_INT_FG = 0xFF; // Clear interrupt flag
    }
  }
}
#pragma restore

void USBDeviceCfg() {
  USB_CTRL = 0x00;            // Clear USB control register
  USB_CTRL &= ~bUC_HOST_MODE; // This bit is the device selection mode
  USB_CTRL |= bUC_DEV_PU_EN | bUC_INT_BUSY |
              bUC_DMA_EN; // USB device and internal pull-up enable,
                          // automatically return to NAK before interrupt flag
                          // is cleared during interrupt
  USB_DEV_AD = 0x00;      // Device address initialization
  //     USB_CTRL |= bUC_LOW_SPEED;
  //     UDEV_CTRL |= bUD_LOW_SPEED; //Run for 1.5M
  USB_CTRL &= ~bUC_LOW_SPEED;
  UDEV_CTRL &= ~bUD_LOW_SPEED; // Select full speed 12M mode, default mode
#if defined(CH551) || defined(CH552) || defined(CH549)
  UDEV_CTRL = bUD_PD_DIS; // Disable DP/DM pull-down resistor
#endif
#if defined(CH559)
  UDEV_CTRL = bUD_DP_PD_DIS; // Disable DP/DM pull-down resistor
#endif
  UDEV_CTRL |= bUD_PORT_EN; // Enable physical port
}

void USBDeviceIntCfg() {
  USB_INT_EN |= bUIE_SUSPEND;  // Enable device hang interrupt
  USB_INT_EN |= bUIE_TRANSFER; // Enable USB transfer completion interrupt
  USB_INT_EN |= bUIE_BUS_RST;  // Enable device mode USB bus reset interrupt
  USB_INT_FG |= 0x1F;          // Clear interrupt flag
  IE_USB = 1;                  // Enable USB interrupt
  EA = 1;                      // Enable global interrupts
}

void USBDeviceEndPointCfg() {
#if defined(CH559)
  // CH559 use differend endianness for these registers
  UEP0_DMA_H = ((uint16_t)Ep0Buffer >> 8); // Endpoint 0 data transfer address
  UEP0_DMA_L = ((uint16_t)Ep0Buffer >> 0); // Endpoint 0 data transfer address
  UEP1_DMA_H = ((uint16_t)Ep1Buffer >> 8); // Endpoint 1 data transfer address
  UEP1_DMA_L = ((uint16_t)Ep1Buffer >> 0); // Endpoint 1 data transfer address
#else
  UEP0_DMA = (uint16_t)Ep0Buffer; // Endpoint 0 data transfer address
  UEP1_DMA = (uint16_t)Ep1Buffer; // Endpoint 1 data transfer address
#endif

  UEP1_CTRL = bUEP_AUTO_TOG | UEP_T_RES_NAK |
              UEP_R_RES_NAK; // Endpoint 1 stays inactive until configured.
  UEP4_1_MOD = 0XC0;         // endpoint1 TX RX enable
  UEP0_CTRL =
      UEP_R_RES_ACK | UEP_T_RES_NAK; // Manual flip, OUT transaction returns
                                     // ACK, IN transaction returns NAK
}
