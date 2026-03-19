// Next available MSG number is    15 
// MODULE_ID BASE_DCL_
/* Next available MSG number is  24 */

//
//////////////////////////////////////////////////////////////////////////////
//
//  Copyright 2024 Autodesk, Inc.  All rights reserved.
//
//  Use of this software is subject to the terms of the Autodesk license 
//  agreement provided at the time of installation or download, or which 
//  otherwise accompanies this software in either electronic or hard copy form.   
//
//////////////////////////////////////////////////////////////////////////////
//
//
//     BASE.DCL      Version 1.1
//
//////////////////////////////////////////////////////////////////////////////
//
// Define common prototypes and subassemblies for use by
// ACAD.DCL and user-defined dialogs (AutoCAD), and by
// ACLT.DCL (AutoCAD LT).

// (The primitive widgets are set up automatically by init_dialog.  The
// equivalent DCL is shown here (commented out) for reference.)

// dialog {
//      layout          = vertical;
//      is_enabled      = false;
// }
// 
// cluster {
//      layout          = horizontal;
// }
// 
// radio_cluster {
//      layout          = horizontal;
//      is_enabled      = true;
// }
// 
// tile {
//      layout          = horizontal;
//      is_enabled      = true;
// }
// 
// text  : tile {
//      fixed_height    = true;       // inhibit vertical expansion
// }
// 
// image : tile {
// }
// 
// button : tile {
//      fixed_height    = true;
//      is_tab_stop     = true;
// }
// 
// image_button : button {
//      is_tab_stop     = true;
// }
// 
// toggle : tile {
//      fixed_height    = true;
//      is_tab_stop     = true;
// }
// 
// radio_button : tile {
//      fixed_height    = true;
//      is_tab_stop     = true;
// }
// 
// list_box : tile {
//      is_tab_stop     = true;
//      height          = 10;
//      width           = 10;
// }
// 
// edit_box : tile {
//      fixed_height    = true;
//      is_tab_stop     = true;
// }
// 
// popup_list : tile {
//      is_tab_stop     = true;
//      fixed_height    = true;
// }
// 
// slider : tile {
//      is_tab_stop     = true;
// }
// 
// spacer : tile {
// }

//----- Styles of clusters.

row : cluster {
    horizontal_margin = none;
    vertical_margin = none;
    children_alignment = centered;
}

column : cluster {
    layout = vertical;
    horizontal_margin = none;
    vertical_margin = none;
}

boxed_row : cluster {
    label = " ";
    boxed = true;
    children_alignment = centered;
}

boxed_column : cluster {
    layout = vertical;
    label = " ";
    boxed = true;
}

//----- Styles of radio clusters.

radio_row : radio_cluster {
    horizontal_margin = none;
    vertical_margin = none;
    children_alignment = centered;
}

radio_column : radio_cluster {
    layout = vertical;
    horizontal_margin = none;
    vertical_margin = none;
}

boxed_radio_row : radio_cluster {
    label = " ";
    boxed = true;
    children_alignment = centered;
}

boxed_radio_column : radio_cluster {
    layout = vertical;
    label = " ";
    boxed = true;
}

//----- Horizontal and vertical blocks of running text.

concatenation : cluster {
    fixed_width = true;
    fixed_height = true;
    children_alignment = centered;
}

paragraph : cluster {
    layout = vertical;
    fixed_height = true;
}

text_part : text {
    horizontal_margin = none;
    vertical_margin = none;
}

text_25 : text {
    width = 25;
}

//----- Common spacers.

spacer_0 : spacer {
    height = 0;
    width = 0;
    horizontal_margin = none;
    vertical_margin = none;
}

spacer_1 : spacer {
    height = 1;
    width = 1;
    horizontal_margin = none;
    vertical_margin = none;
}

//----- The normal default widget.

default_button : button {
        is_default      = true;
}

//----- Standard prototype for making consistent "dialog retirement buttons".
//      Used below for the predefined retirement buttons, and for user-defined
//      dialogs that need retirement buttons with specialized verbs.

retirement_button : button {
        fixed_width     = true;
        width           = 8;
        alignment = centered;
}

//----- Standard dialog retirement buttons.  Unless one is building a dialog
//      retirement subassembly containing specialized verbs, these will 
//      normally not be used directly by DCL code outside of base.dcl; use
//      the pre-built subassemblies in the next section.

ok_button : retirement_button {
        label           = "  OK  ";
        key             = "accept";
        is_default      = true;
}

cancel_button : retirement_button {
        label           = "Cancel";
        key             = "cancel";
        is_cancel       = true;
}

help_button : retirement_button {
        label           = "&Help";
        key             = "help";
        is_help         = true;
}

info_button : retirement_button {
        label           = "&Info...";
        key             = "info";
}

//----- Pre-built arrays of dialog bottom-line buttons.

ok_only : column {
    fixed_width = true;
    alignment = centered;
    : ok_button {
        is_cancel = true;
    }
}

ok_cancel : column {
    : row {
        fixed_width = true;
        alignment = centered;
        ok_button;
        : spacer { width = 2; }
        cancel_button;
    }
}

ok_cancel_help : column {
    : row {
        fixed_width = true;
        alignment = centered;
        ok_button;
        : spacer { width = 2; }
        cancel_button;
        : spacer { width = 2; }
        help_button;
    }
}

ok_cancel_help_info : column {
    : row {
        fixed_width = true;
        alignment = centered;
        ok_button;
        : spacer { width = 2; }
        cancel_button;
        : spacer { width = 2; }
        help_button;
        : spacer { width = 2; }
        info_button;
    }
}

//----- Error reporting tiles.

errtile : text {
        label = "";
        key = "error";
        width = 35;                   // must be long enough to hold error msgs
        is_error_tile = true;
}

// A custer consisting of OK, Cancel, and Help on one line with the error tile
//    below.

ok_cancel_help_errtile : column {
    ok_cancel_help;
    errtile;
}

// The same thing without the Help button for subdialogues that have no help
//    available.

ok_cancel_err : column {
    ok_cancel;
    errtile;
}

//----- Currently, the only dcl setting is the audit_level which controls the
//  level of semantic error checking applied during a load_dialog operation.
//      (0 = none, 1 = errors, 2 = warnings, 3 = hints)
// See AutoCAD's README for details.
default_dcl_settings : tile {
    audit_level = 1;
}

//----- Miscellaneous parts used by ACAD.DCL (AutoCAD) & ACLT.DCL (AutoCAD LT).

image_block : image {
        key = "show_image";
        height = 1;
        width = 1;
}

icon_image : image_button {
        color                   = 0;
        width                   = 12;
        aspect_ratio            = 0.66;
        allow_accept            = true;
        fixed_height            = true;
        fixed_width             = true;
}

edit12_box : edit_box {
        edit_width = 12;
        edit_limit = 148;    // 18 * 8 (CIF/MIF size)
}

edit32_box : edit_box {
        edit_width = 32;
        edit_limit = 2048;   // MAX_VALUE (256 * 8 (CIF/MIF size))
}

//  The following are for the color-selection dialogs

swatch : image_button {
    vertical_margin = none;
    horizontal_margin = none;
    fixed_height = true;
    fixed_width = true;
    height = 1.5;
    width = 3;
        allow_accept = true;
}

color_palette_1_7 : row {             // Standard colors 1-7
    : swatch { color = 001; key = "001"; }
    : swatch { color = 002; key = "002"; }
    : swatch { color = 003; key = "003"; }
    : swatch { color = 004; key = "004"; }
    : swatch { color = 005; key = "005"; }
    : swatch { color = 006; key = "006"; }
    : swatch { color = 007; key = "007"; }
}

color_palette_1_9 : row {             // Standard colors, plus 8 and 9
    color_palette_1_7;
    : swatch { color = 008; key = "008"; }
    : swatch { color = 009; key = "009"; }
}

color_palette_0_9 : row {             // Standard colors, plus 0, 8, and 9
    : swatch { color = 000; key = "000"; }
    color_palette_1_9;
}

color_palette_250_255 : row {         // Grey shades 250-255
    : swatch { color = 250; key = "250"; }
    : swatch { color = 251; key = "251"; }
    : swatch { color = 252; key = "252"; }
    : swatch { color = 253; key = "253"; }
    : swatch { color = 254; key = "254"; }
    : swatch { color = 255; key = "255"; }
}

std_rq_color :column{
    :column {
        :boxed_row {
            fixed_width = true;
            label = "Standard Colors";
            color_palette_1_9;
        }
        :row {
            :boxed_row {
                fixed_width = true;
                label = "Gray Shades";
                color_palette_250_255;
            }
            :boxed_row {
                fixed_width = true;
                label = "Logical Colors";
                :button {
                    label = "BY&LAYER";
                    key = "256";
                                }
                :button {
                    label = "BY&BLOCK";
                    key = "000";
                }
            }
        }
    }
    :boxed_column {
        label = "Full Color Palette";
        :image_button{
            key = "hiside";
            alignment = centered;
            width = 40;
            height = 4;
                        allow_accept = true;
            is_enabled = false;
        }
        :image_button{
            alignment = centered;
            key = "loside";
            width = 40;
            height = 4;
                        allow_accept = true;
            is_enabled = false;
        }
    }
    :row {
        fixed_width = true;
        alignment = centered;
        children_alignment = bottom;
        :edit12_box {
            label = "Color:";
            key = "color_edit";
            allow_accept = true;
        }
        :swatch {
            key = "color_image";
        }
    }
    ok_cancel_help_errtile;
}

//  The preceding are for color-selection dialogs


//  Top and bottom sub-assemblies for the files dialogue
files_topdf : column {
    : edit_box {
        key = "pedit";
        label = "&Pattern:";
        edit_width = 35;
    }
    : row {
        : text {
            label = "Directory:";
        }
        : text {
            key = "dirtext";
            width = 35;
        }
    }
}

files_bottomdf : column {
    : edit_box {
        key = "fedit";
        label = "&File:";
        allow_accept = true;
    }
    ok_cancel;
    errtile;
}

fcf_ibut : image_button {
    horizontal_margin = none;
    width = 3.5;
    height = 1.2;
    color        = 0;
    alignment = bottom;
}

fcf_ebox : edit_box {
    horizontal_margin = none;
    edit_width = 7;
    fixed_width = true;
    alignment = bottom;
}

fcf_ebox1 : edit_box {
    horizontal_margin = none;
    edit_width = 3;
    edit_limit = 3;
    fixed_width = true;
    alignment = bottom;
}

fcf_ibut1 : image_button {
    width        = 5.0;
    aspect_ratio = 0.66;
    color        = 0;
    allow_accept = true;
}
