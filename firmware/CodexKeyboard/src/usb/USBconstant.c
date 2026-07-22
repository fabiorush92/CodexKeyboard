// USB identity and descriptors for the single vendor-defined collection.
#include "USBconstant.h"

__code USB_Descriptor_Device_t DeviceDescriptor = {
    .Header = {.Size = sizeof(USB_Descriptor_Device_t), .Type = DTYPE_Device},
    .USBSpecification = VERSION_BCD(1, 1, 0),
    .Class = 0x00,
    .SubClass = 0x00,
    .Protocol = 0x00,
    .Endpoint0Size = DEFAULT_ENDP0_SIZE,
    .VendorID = 0x1209,
    .ProductID = 0xC55D,
    .ReleaseNumber = VERSION_BCD(1, 1, 0),
    .ManufacturerStrIndex = 1,
    .ProductStrIndex = 2,
    .SerialNumStrIndex = 3,
    .NumberOfConfigurations = 1};

__code USB_Descriptor_Configuration_t ConfigurationDescriptor = {
    .Config = {.Header = {.Size = sizeof(USB_Descriptor_Configuration_Header_t),
                          .Type = DTYPE_Configuration},
               .TotalConfigurationSize = sizeof(USB_Descriptor_Configuration_t),
               .TotalInterfaces = 1,
               .ConfigurationNumber = 1,
               .ConfigurationStrIndex = NO_DESCRIPTOR,
               .ConfigAttributes = USB_CONFIG_ATTR_RESERVED,
               .MaxPowerConsumption = USB_CONFIG_POWER_MA(200)},
    .HID_Interface = {.Header = {.Size = sizeof(USB_Descriptor_Interface_t),
                                 .Type = DTYPE_Interface},
                      .InterfaceNumber = 0,
                      .AlternateSetting = 0,
                      .TotalEndpoints = 2,
                      .Class = HID_CSCP_HIDClass,
                      .SubClass = HID_CSCP_NonBootSubclass,
                      .Protocol = HID_CSCP_NonBootProtocol,
                      .InterfaceStrIndex = NO_DESCRIPTOR},
    .HID_Descriptor = {.Header = {.Size = sizeof(USB_HID_Descriptor_HID_t),
                                  .Type = HID_DTYPE_HID},
                       .HIDSpec = VERSION_BCD(1, 1, 0),
                       .CountryCode = 0,
                       .TotalReportDescriptors = 1,
                       .HIDReportType = HID_DTYPE_Report,
                       .HIDReportLength = sizeof(ReportDescriptor)},
    .HID_ReportINEndpoint = {.Header = {.Size = sizeof(USB_Descriptor_Endpoint_t),
                                        .Type = DTYPE_Endpoint},
                             .EndpointAddress = HID_IN_EPADDR,
                             .Attributes = EP_TYPE_INTERRUPT |
                                           ENDPOINT_ATTR_NO_SYNC |
                                           ENDPOINT_USAGE_DATA,
                             .EndpointSize = HID_REPORT_LENGTH,
                             .PollingIntervalMS = 10},
    .HID_ReportOUTEndpoint = {.Header = {.Size = sizeof(USB_Descriptor_Endpoint_t),
                                         .Type = DTYPE_Endpoint},
                              .EndpointAddress = HID_OUT_EPADDR,
                              .Attributes = EP_TYPE_INTERRUPT |
                                            ENDPOINT_ATTR_NO_SYNC |
                                            ENDPOINT_USAGE_DATA,
                              .EndpointSize = HID_REPORT_LENGTH,
                              .PollingIntervalMS = 10}};

__code uint8_t ReportDescriptor[] = {
    0x06, 0x00, 0xFF, // Usage page 0xFF00
    0x09, 0x01,       // Usage 1
    0xA1, 0x01,       // Application collection
    0x85, 0x01,       // Report ID 1
    0x15, 0x00,       // Logical minimum 0
    0x26, 0xFF, 0x00, // Logical maximum 255
    0x75, 0x08,       // Eight-bit fields
    0x95, 0x0F,       // Fifteen fields plus the report ID
    0x09, 0x02,       // Input usage
    0x81, 0x02,       // Input data
    0x95, 0x0F,       // Fifteen output fields
    0x09, 0x03,       // Output usage
    0x91, 0x02,       // Output data
    0xC0};            // End collection

__code uint8_t LanguageDescriptor[] = {0x04, 0x03, 0x09, 0x04};

__code uint16_t ProductDescriptor[] = {
    (((13 + 1) * 2) | (DTYPE_String << 8)),
    'C', 'o', 'd', 'e', 'x', 'K', 'e', 'y', 'b', 'o', 'a', 'r', 'd'};
