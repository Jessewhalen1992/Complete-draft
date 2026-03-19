// Next available MSG number is   207 
// MODULE_ID ASE_DCL_
// 
//----------------------------------------------------------------------------
//
//     ASE.DCL          AutoCAD SQL Environment dialog file
//
//     Copyright (C) 1991-1997 by Autodesk, Inc.
//
//     Permission to use, copy, modify, and distribute this software
//     for any purpose and without fee is hereby granted, provided
//     that the above copyright notice appears in all copies and
//     that both that copyright notice and the limited warranty and
//     restricted rights notice below appear in all supporting
//     documentation.
//
//     AUTODESK PROVIDES THIS PROGRAM "AS IS" AND WITH ALL FAULTS.
//     AUTODESK SPECIFICALLY DISCLAIMS ANY IMPLIED WARRANTY OF
//     MERCHANTABILITY OR FITNESS FOR A PARTICULAR USE.  AUTODESK, INC.
//     DOES NOT WARRANT THAT THE OPERATION OF THE PROGRAM WILL BE
//     UNINTERRUPTED OR ERROR FREE.
//
//     Use, duplication, or disclosure by the U.S. Government is subject to
//     restrictions set forth in FAR 52.227-19 (Commercial Computer
//     Software - Restricted Rights) and DFAR 252.227-7013(c)(1)(ii)
//     (Rights in Technical Data and Computer Software), as applicable.
//
//----------------------------------------------------------------------------
dcl_settings : default_dcl_settings { audit_level = 0; }

ase_ok_button : ok_button {
    key                 = "ID_BUTT_OK";
    is_default          = false ;
}

ase_cancel_button : cancel_button {
    key                 = "ID_BUTT_CANCEL";
    is_cancel           = true ;
}

ase_help_button : help_button {
    key                 = "ID_BUTT_HELP";
}

ase_errtile : errtile {
    key                 = "ID_TEXT_ERRMSG";
}

ase_close_button : ok_button {
    label               = "&Close";
    key                 = "ID_BUTT_CLOSE";
    is_default          = false ;
    is_cancel           = true ;
}

ase_execute_button : retirement_button {
    label          = "&Execute";
    key            = "ID_BUTT_EXEC";
}

ase_graph_button : retirement_button {
    label          = "&Graphical <";
    key            = "ID_BUTT_GRAPH";
    width          = 10 ;
}

ase_upd_button : retirement_button {
    key            = "ID_BUTT_UPD";
    label          = "&Update";
    width          = 10 ;
}

ase_delete_button : retirement_button {
    key         = "ID_BUTT_DEL";
    label       = "&Delete";
    width       = 10 ;
}

ase_keys_button : retirement_button {
    label       = "&Keys...";
    key         = "ID_BUTT_KEYS";
    width       = 10 ;
}

ase_next_button : retirement_button {
    label       = "&Next";
    key         = "ID_BUTT_NEXT";
    width       = 8 ;
}

ase_prior_button : retirement_button {
    label       = "&Prior";
    key         = "ID_BUTT_PRIOR";
    width       = 8 ;
}

ase_first_button : retirement_button {
    label       = "&First";
    key         = "ID_BUTT_FIRST";
    width       = 8 ;
}

ase_last_button : retirement_button {
    label       = "&Last";
    key         = "ID_BUTT_LAST";
    width       = 8 ;
}

ase_select_button : retirement_button {
    label          = "&Select";
    key            = "ID_BUTT_SELECT";
    width          = 10 ;
}

ase_edit_button : retirement_button {
    label       = "&Edit...";
    key         = "ID_BUTT_EDIT";
    width       = 10 ;
}

ase_hlight_button : retirement_button {
   label        = "H&ighlight <";
   key          = "ID_BUTT_HLIGHT";
   width        = 10 ;
}


ase_ok_cancel : column {
    : row {
        fixed_width = true;
        alignment = centered;
        ase_ok_button;
        : spacer { width = 2; }
        ase_cancel_button;
    }
}

ase_ok_cancel_help : column {
    : row {
        fixed_width = true;
        alignment = centered;
        ase_ok_button;
        : spacer { width = 2; }
        ase_cancel_button;
        : spacer { width = 2; }
        ase_help_button;
    }
}

curr_path_string : row {
   : text {
        key     = "ID_TEXT_CURRSET";
   }
}

ase_sql_state : column {
   : row {
       : text {
           label      = "SQL STATE: " ;
           width      = 10 ;
           fixed_width= true ;
           alignment  = left ;
       }
       : text {
           alignment  = left ;
           width      = 25 ;
           key        = "ID_TEXT_SQLS" ;
       }
   }
}

//------------------------------------------------------
//
//  Macro for ASE current settings tile
//
current_settings : column {
    : boxed_row {
        label = "Database Object Settings";
        curr_path_string ;
    }
}

lpn_plist : row {
    : column {
          : text {
             label      = "Link Path Name";
             width      = 14 ;
          }
          : popup_list {
             width      = 14 ;
             key        = "ID_PLIST_LPN" ;
          }
    }
}

//------------------------------------------------------
//
//  Macro for Environment, Catalog, Schema poup-lists row
//
env_cat_sch : column {
    : row {
        : column {
              alignment         = left ;
              : text {
                label           = "Environment";
                 width          = 14 ;
              }
              : popup_list {
                width           = 14 ;
                key             = "ID_PLIST_ENV";
              }
        }
        : column {
              : text {
                 label          = "Catalog   ";
                 width          = 14 ;
              }
              : popup_list {
                 width          = 14 ;
                 key            = "ID_PLIST_CAT";
              }
        }
        : column {
              : text {
                 label          = "Schema    ";
                 width          = 14 ;
              }
              : popup_list {
                 width          = 14 ;
                 key            = "ID_PLIST_SCH";
              }
        }
    }
}

//------------------------------------------------------
//
//  Macro for Environment, Catalog, Schema, Table poup-lists row
//
env_cat_sch_tab : column {
    : row {
        env_cat_sch;
        : column {
              : text {
                 label  = "Table";
                 width  = 14 ;
              }
              : popup_list {
                 width  = 14 ;
                 key    = "ID_PLIST_TBL";
              }
        }
    }
}

//------------------------------------------------------
//
//  Macro for Environment, Catalog, Schema, Table, Link Path Name
//  poup-lists row.
//
env_cat_sch_tab_lpn : column {
    : row {
        env_cat_sch_tab ;
        lpn_plist ;
    }
}
iso_level_list : row {
    : column {
          : text {
             label      = "Isolation &Level";
             width      = 14 ;
          }
          : popup_list {
             width      = 14 ;
             key        = "ID_PLIST_ISO" ;
          }
    }
}

//---------------------------------------------------------------------------
// Macro for the SQL statement
//
ase_sql_statement : row {
    : column {
        : text {
            label  = "SELECT Statement:";
        }
        : text {
            key    = "ID_TEXT_SQLS";
            alignment = left ;
        }
        : slider {
            key    = "ID_SLDR_ERRMSG";
            layout = horizontal ;
            min_value  = 0;
            max_value  = 255;
        }
    }
}

//------------------------------------------------------------------------------------
// ASEADMIN dialog box
//
aseadmin : dialog {
    aspect_ratio = 0;
    label = "Administration";
    : row {
        : column {
            : column {
                : boxed_row {
                    label       = "Database Object Settings";
                    curr_path_string ;
                }
            }
            : boxed_radio_row {
                label   = "Database Object Selection" ;
                : radio_button {
                    label       = "En&vironment";
                    key         = "ID_RBUT_ENV";
                }
                : radio_button {
                    label       = "Catal&og";
                    key         = "ID_RBUT_CAT";
                }
                : radio_button {
                    label       = "Sche&ma";
                    key         = "ID_RBUT_SCH";
                }
                : radio_button {
                    label       = "&Table";
                    key         = "ID_RBUT_TBL";
                }
            }
            : concatenation {
                children_fixed_width = true ;
                : text {
                     label      = "Data&base Objects: " ;
                     width      = 30 ;
                }
                : text {
                     width      = 5 ;
                     label      = "Con";
                }
                : text {
                     width      = 5 ;
                     label      = "Avl";
                }
                : text {
                     width      = 5 ;
                     label      = "Reg";
                }
                : text {
                     width      = 2 ;
                }
            }
            : list_box {
                horizontal_alignment = centered ;
                vertical_alignment = centered ;
                key             = "ID_LBOX_DO";
                mnemonic        = "b" ;
                height          = 6 ;
                tabs            = "32 38 44 48" ;
                tab_truncate    = true ;
                fixed_width_font= true ;
            }
            : slider {
                key             = "ID_SLIDER";
                layout          = horizontal ;
                min_value       = 0;
                max_value       = 255;
            }
        }
        : column {
            : boxed_column {
                label               = "Set by" ;
                children_fixed_width= true ;
                children_alignment  = centered ;
                : ase_graph_button {
                   width = 14;
                }
                lpn_plist ;
            }
            : column {
                : button {
                      label         = "&Connect...";
                      key           = "ID_BUTT_CONNECT";
                      width         = 10 ;
                }
                : button {
                      label         = "&Disconnect";
                      key           = "ID_BUTT_DISCON";
                      width         = 10 ;
                }
                : button {
                      label         = "&About Env...";
                      key           = "ID_BUTT_ABOUT";
                }
                iso_level_list ;
            }
        }
    }
    : edit_box {
        label           = "&Path: ";
        key             = "ID_EBOX_DO";
    }
    : row {
        children_fixed_width = true ;
        : button {
            label         = "Lin&k Path Names...";
            key           = "ID_BUTT_KEYS";
            width         = 15 ;
        }
        : button {
            label         = "&Erase Links <";
            key           = "ID_BUTT_ERASE";
            width         = 15 ;
        }
        : button {
            label         = "&Reload DA <";
            key           = "ID_BUTT_RELDA";
            width         = 15 ;
        }
        : button {
            label         = "&Synchronize...";
            key           = "ID_BUTT_SYNCH";
            width         = 15 ;
        }
    }
    ase_ok_cancel_help;
    ase_errtile;
}

//--------------------------------------------------------------------------
// ASE About dialogue box.
//
about : dialog {
    aspect_ratio = 0;
    label = "About Environment";
    : column {
         children_alignment      = centered ;
         children_fixed_width    = true ;
         : text {
              key        = "ID_TEXT_ASIV" ;
              width      = 65 ;
         }
         : text {
              key        = "ID_TEXT_ASEV" ;
              width      = 65 ;
         }
         : text {
              key        = "ID_TEXT_ACPR" ;
              width      = 65 ;
         }
         : boxed_column {
             : row {
                 : text {
                     label      = "Connected Environment: " ;
                     width      = 25 ;
                 }
                 : text {
                     key        = "ID_TEXT_ENV" ;
                     width      = 10 ;
                     allignment = left ;
                 }
                 : text {
                     key        = "ID_TEXT_ENVC" ;
                     allignment = left ;
                     width      = 30 ;
                 }
             }
             : row {
                 : text {
                     label      = "Loaded Driver" ;
                     width      = 25 ;
                 }
                 : text {
                     key    = "ID_TEXT_DRV" ;
                     width  = 10 ;
                     allignment = left ;
                 }
                 : text {
                     key        = "ID_TEXT_DRVC" ;
                     allignment = left ;
                     width      = 30 ;
                 }
             }
             : list_box {
                 label      = "&Capabilities: " ;
                 key        = "ID_LBOX_DRV" ;
                 width      = 65 ;
                 fixed_width_font = true ;
                 height     = 8 ;
                 allow_accept = true ;
                 tabs       = "55 60" ;
                 tab_truncate    = true ;
             }
         }
         : ase_ok_button {is_default = true ; is_cancel = true ;}
    }
}

//--------------------------------------------------------------------------
// Connect to Environment dialogue box.
//
connect : dialog {
    aspect_ratio        = 0;
    label               = "Connect to Environment";
    initial_focus       = "ID_EBOX_UNAME";
    : row {
        : column {
            : text {
               label      = "Environment:";
               alignment   = left ;
            }
            : text {
               label       = "User Name:";
               alignment   = left ;
            }
            : text {
               label       = "Password:";
               alignment   = left ;
            }
        }
        : column {
            : text {
                 key        = "ID_TEXT_ENV";
                 width      = 20 ;
                 alignment  = left ;
                 fixed_width= true ;
            }
            : edit_box  {
               key         = "ID_EBOX_UNAME";
               edit_width  = 20 ;
               allow_accept= true ;
               fixed_width = true ;
            }
            : edit_box  {
               key         = "ID_EBOX_PWD";
               edit_width  = 20 ;
               allow_accept= true ;
               fixed_width = true ;
               password_char = "*" ;
            }
        }
    }
    : row {
        fixed_width = true;
        alignment = centered;
        : ase_ok_button {is_default = true ; }
        ase_cancel_button;
        ase_help_button ;
    }
    ase_errtile;
}

//---------------------------------------------------------------------
//
//  Set Key Columns dialogue box
//
setkeys : dialog {
    aspect_ratio = 0;
    label = "Link Path Names";
    current_settings ;
    : boxed_row {
        label  = "Key Selection" ;
        : column {
            children_fixed_width = true ;
            : concatenation {
                : text {
                    label       = "&State:" ;
                    width       = 6 ;
                    fixed_width = true ;
                }
                : text {
                    key         = "ID_TEXT_COLLABEL" ;
                    alignment   = left ;
                    width       = 20 ;
                    fixed_width = true ;
                }
                : text {
                    label       = "Data Type:" ;
                    alignment   = left ;
                }
            }
            : list_box {
                alignment       = left ;
                width           = 55 ;
                height          = 5 ;
                fixed_width_font= true ;
                tabs            = "8 29 33 37 42" ;
                tab_truncate    = true ;
                key             = "ID_LBOX_COLS";
                mnemonic        = "S" ;
            }
            : slider {
                key             = "ID_SLIDER";
                layout          = horizontal ;
                min_value       = 0;
                max_value       = 255;
                width           = 55 ;
            }
        }
        : column {
             : spacer {
                height          = 1 ;
             }
             : button {
                 label          = "&On";
                 key            = "ID_BUTT_ON";
                 width          = 8 ;
                 fixed_width    = true ;
             }
             : button {
                 label          = "Of&f";
                 key            = "ID_BUTT_OFF";
                 width          = 8 ;
                 fixed_width    = true ;
             }
             : spacer {
                height          = 1 ;
             }
        }
    }
    : boxed_row {
        label  = "Link Path" ;
        : column {
             children_fixed_width = true ;
             children_alignment   = left ;
             : row {
                 : text {
                    label       = "Ne&w:" ;
                    width       = 10 ;
                    alignment   = centered ;
                 }
                 : edit_box {
                    key         = "ID_EBOX_LPN" ;
                    mnemonic    = "w" ;
                    width       = 30 ;
                 }
             }
             : row {
                 : text {
                    label       = "E&xisting:" ;
                    width       = 10 ;
                    alignment   = centered ;
                 }
                 : popup_list {
                    key         = "ID_PLIST_LPN" ;
                    mnemonic    = "x" ;
                    width       = 30 ;
                 }
             }
        }
        : column {
             : button {
                 label          = "&New";
                 key            = "ID_BUTT_NEW";
                 width          = 10 ;
                 fixed_width    = true ;
             }
             : button {
                 label          = "&Erase";
                 key            = "ID_BUTT_ERASE";
                 width          = 10 ;
                 fixed_width    = true ;
             }
        }
        : column {
             : button {
                 label          = "&Rename";
                 key            = "ID_BUTT_RENAME";
                 width          = 10 ;
                 fixed_width    = true ;
             }
             : button {
                 label          = "Erase &All" ;
                 key            = "ID_BUTT_ERALL";
                 width          = 10 ;
                 fixed_width    = true ;
             }
        }
    }
    : row {
         : spacer {width = 15 ;}
         ase_close_button;
         ase_help_button;
         : spacer {width = 15 ;}
    }
    ase_errtile;
}

//---------------------------------------------------------------------
//
//  Synchronization Report dialogue box
//
synchro : dialog {
    aspect_ratio = 0;
    label = "Synchronize Links";
    : list_box {
        alignment       = left ;
        label           = "Message &List: ";
        width           = 55;
        key             = "ID_LBOX_MESSAGE";
        multiple_select = true ;
        fixed_width_font= true ;
    }
    : slider {
        key             = "ID_SLDR_ERRMSG";
        layout          = horizontal ;
        min_value       = 0;
        max_value       = 255;
    }
    : column {
        : row {
             fixed_width = true;
             alignment = centered;
             : button {
                width      = 12 ;
                label      = "Select &All";
                key        = "ID_BUTT_SALL";
             }
             ase_hlight_button ;
             : button {
                width      = 12 ;
                label      = "&More Info...";
                key        = "ID_BUTT_MORE";
             }
             : button {
                width      = 12 ;
                label      = "&Synchronize";
                key        = "ID_BUTT_SYNCH";
             }
        }
        : row {
             : spacer {width = 10 ;}
             ase_close_button;
             ase_help_button;
             : spacer {width = 10 ;}
        }
    }
    ase_errtile;
}

//---------------------------------------------------------------------
//
//  ASEROW dialogue box
//
aserows : dialog {
    aspect_ratio = 0;
    label = "Rows";
    : boxed_row {
        label      = "Database Ob&ject Settings";
        children_fixed_width = true ;
        : column {
            alignment   = left ;
            : text {
              label     = "Environment";
              width     = 14 ;
            }
            : popup_list {
              width     = 14 ;
              key       = "ID_PLIST_ENV";
            }
        }
        : column {
            : text {
              label     = "Catalog";
              width     = 14 ;
            }
            : popup_list {
              width     = 14 ;
              key       = "ID_PLIST_CAT";
            }
        }
        : column {
            : text {
              label     = "Schema";
              width     = 14 ;
            }
            : popup_list {
              width     = 14 ;
              key       = "ID_PLIST_SCH";
            }
        }
        : column {
            : text {
               label    = "Table";
               width    = 14 ;
            }
            : popup_list {
               width    = 14 ;
               key      = "ID_PLIST_TBL";
            }
        }
        : column {
            : text {
               label    = "Link Path Name";
               width    = 14 ;
            }
            : popup_list {
               width    = 14 ;
               key      = "ID_PLIST_LPN" ;
            }
        }
    }
    : row {
        : boxed_radio_column {
             label          = "Cursor State" ;
             : radio_button {
                 key        = "ID_RBUT_RDONL";
                 label      = "&Read-only";
             }
             : radio_button {
                 key        = "ID_RBUT_SCROL";
                 label      = "&Scrollable";
             }
             : radio_button {
                 key        = "ID_RBUT_UPD";
                 label      = "&Updatable";
             }
        }
        : boxed_column {
            fixed_width     = true ;
            label           = "SELECT Rows" ;
            : edit_box {
                alignment      = left ;
                label          = "&Condition:";
                key            = "ID_EBOX_COND";
                edit_limit     = 256 ;
            }
            : row {
                children_fixed_width = true ;
                : button {
                    label      = "&Open Cursor";
                    key        = "ID_BUTT_SELECT";
                    width      = 13 ;
                }
                : button {
                    label      = "Key &Values...";
                    width      = 13 ;
                    key        = "ID_BUTT_KEYS";
                }
                : ase_graph_button { width = 13 ; }
                : button {
                    key        = "ID_BUTT_CLCURS";
                    label      = "&Close Cursor";
                    width      = 13 ;
                }
            }
        }
    }
    : row {
        : column {
             : list_box {
                 alignment      = left ;
                 key            = "ID_LBOX_CURSOR";
                 height         = 6 ;
                 width          = 65 ;
                 fixed_width_font = true ;
                 tabs           = "20 25 30 35" ;
                 tab_truncate   = true ;
             }
             : slider {
                 key            = "ID_SLIDER";
                 layout         = horizontal ;
                 min_value      = 0 ;
                 max_value      = 255 ;
                 width          = 65 ;
             }
        }
        : column {
            alignment           = left ;
            children_fixed_width= true ;
            ase_next_button ;
            ase_prior_button ;
            ase_first_button ;
            ase_last_button ;
        }
    }
    : row {
        : button {
            label      = "&Make Link <";
            key        = "ID_BUTT_MLINK";
            width      = 11 ;
            fixed_width= true ;
        }
        : button {
            label      = "Make &DA...";
            key        = "ID_BUTT_MDA";
            width      = 11 ;
            fixed_width= true ;
        }
        : button {
            label      = "&Select <";
            key        = "ID_BUTT_SSET";
            width      = 11 ;
            fixed_width= true ;
        }
        : button {
            label      = "&Unselect <";
            key        = "ID_BUTT_USET";
            width      = 11 ;
            fixed_width= true ;
        }
        : button {
            label      = "Lin&ks...";
            key        = "ID_BUTT_LINKS";
            width      = 11 ;
            fixed_width= true ;
        }
        : ase_edit_button {width = 11; }
    }
    ase_ok_cancel_help;
    ase_errtile;
}

//---------------------------------------------------------------------
//
//  Find Row by Key Values dialogue box
//
findrow : dialog {
    aspect_ratio = 0;
    label = "Select Row by Key Values";
    : column {
        : concatenation {
             : text {
                label      = "&Key Columns:";
                width      = 21 ;
             }
             : text {
                label      = "Values: ";
                width      = 39 ;
             }
        }
        : list_box {
             alignment  = left ;
             key        = "ID_LBOX_KEYS";
             mnemonic   = "K" ;
             tab_truncate = true ;
             tabs       = "20 25 30 35" ;
             tab_truncate  = true ;
             width      = 60 ;
             fixed_width_font = true ;
        }
        : slider {
             key        = "ID_SLIDER";
             layout     = horizontal ;
             min_value  = 0;
             max_value  = 255;
             width      = 60 ;
        }
        : column {
            : row {
                : text {
                    label       = "Name: ";
                    width       = 6 ;
                    fixed_width = true ;
                }
                : text {
                    alignment   = left ;
                    key         = "ID_TEXT_COLUMN" ;
                    width       = 53 ;
                }
            }
            : row {
                : edit_box {
                    alignment   = left ;
                    label       = "&Value: ";
                    key         = "ID_EBOX_KVAL";
                    edit_width  = 53 ;
                }
            }
        }
    }
    ase_ok_cancel_help;
    ase_errtile;
}

//---------------------------------------------------------------------
//
//  Edit Row
//
editrow : dialog {
    aspect_ratio = 0;
    label = "Edit Row";
    : column {
        ase_sql_statement ;
        : column {
            : list_box {
                  alignment     = left ;
                  width         = 60 ;
                  height        = 7 ;
                  key           = "ID_LBOX_COLS";
                  tabs          = "20 25 30 35" ;
                  tab_truncate  = true ;
                  fixed_width_font = true ;
            }
            : slider {
                  key           = "ID_SLIDER";
                  layout        = horizontal ;
                  min_value     = 0;
                  max_value     = 255;
                  width         = 60 ;
            }
            : column {
                : row {
                    : text {
                        label   = "Name: ";
                        width   = 6 ;
                        fixed_width = true ;
                    }
                    : text {
                        alignment   = left ;
                        key         = "ID_TEXT_COLUMN" ;
                        width       = 54 ;
                    }
                }
                : row {
                    : edit_box {
                        alignment   = left ;
                        label       = "&Value: ";
                        key         = "ID_EBOX_KVAL";
                    }
                }
            }
            : row {
               alignment        = centered ;
               fixed_width      = true ;
               ase_upd_button;
               : spacer {width  = 2;}
               : button {
                   key          = "ID_BUTT_INSERT";
                   label        = "&Insert";
               }
               : spacer {width  = 2;}
               ase_delete_button;
            }
        }
        : row {
            alignment           = centered ;
            fixed_width         = true ;
            ase_close_button ;
            : spacer {width     = 2;}
            ase_help_button;
        }
    }
    ase_errtile;
}

//---------------------------------------------------------------------
//
//  Confirmation1
//
confirm_1 : dialog {
    aspect_ratio = 0;
    label = "Confirm" ;
    : column {
       children_fixed_width   = true ;
       children_alignment     = centered ;
       : column {
            : text {
                  alignment   = centered ;
                  key         = "ID_TEXT_MSG";
                  width       = 45 ;
            }
            : text {
                  alignment   = centered ;
                  width       = 45 ;
                  key         = "ID_TEXT_CONF";
            }
       }
       spacer_1 ;
    }
    ase_ok_cancel;
}

//---------------------------------------------------------------------
//
//  Confirm 2
//
confirm_2 : dialog {
    aspect_ratio= 0;
    key         = "ID_DIALOG_CONF2" ;
    : column {
        : text {
           alignment    = centered ;
           key          = "ID_TEXT_OBJECT";
           width        = 45 ;
       }
       : text {
           alignment    = centered ;
           key          = "ID_TEXT_MSG";
           width        = 45 ;
       }
       : text {
           alignment    = centered ;
           key          = "ID_TEXT_CONF";
           width        = 45 ;
       }
       : row {
           fixed_width  = true;
           : spacer {width = 8;}
           : button {
               width = 10;
               label = "&Yes";
               key   = "ID_BUTT_YES";
           }
           : button {
               width = 10;
               label = "Yes to &All";
               key   = "ID_BUTT_YESALL";
           }
           : button {
               width = 10;
               label = "&No";
               key   = "ID_BUTT_NO";
           }
           ase_cancel_button ;
           : spacer {width = 8;}
       }
   }
    ase_errtile;
}

//---------------------------------------------------------------------
//
//  Edit Key Values Confirmation dialogue box
//
keyconf : dialog {
    aspect_ratio = 0;
    label = "Confirm Update or Erase Links" ;
    : column {
       children_fixed_width   = true ;
       children_alignment     = centered ;
       : column {
            : text {
                  alignment   = centered ;
                  key         = "ID_TEXT_MSG";
                  width       = 50 ;
            }
            : text {
                  alignment   = centered ;
                  key         = "ID_TEXT_CONF";
            }
       }
       spacer_1 ;
       : radio_column {
            : radio_button {
                 key          = "ID_RBUT_SYNCH";
                 label        = "&update link(s)?" ;
            }
            : radio_button {
                 key          = "ID_RBUT_DEL";
                 label        = "&erase link(s)?";
            }
       }
       spacer_1 ;
    }
    ase_ok_cancel_help;
}

//---------------------------------------------------------------------
//
//  Make DA dialogue box
//
makeda : dialog {
    aspect_ratio = 0;
    label = "Make Displayable Attribute";
    : column {
        : row {
             : list_box {
                 alignment      = left ;
                 width          = 20 ;
                 height         = 8 ;
                 key            = "ID_LBOX_TBCOL";
                 label          = "&Table Columns:";
                 fixed_width_font = true ;
             }
             : column {
                 spacer_1 ;
                 : button {
                     key        = "ID_BUTT_INSONE";
                     label      = "&Add ->";
                 }
                 : button {
                     key        = "ID_BUTT_REMONE";
                     label      = "Re&move <-";
                 }
                 : button {
                     key        = "ID_BUTT_INSALL";
                     label      = "Add Al&l ->";
                 }
                 : button {
                     key        = "ID_BUTT_REMALL";
                     label      = "Remo&ve All <-";
                 }
             }
             : list_box {
                 alignment      = left ;
                 width          = 20 ;
                 height         = 8 ;
                 key            = "ID_LBOX_DACOL";
                 label          = "&DA Columns:";
                 fixed_width_font = true ;
             }
        }
        : boxed_column {
             label = "Format" ;
             : row {
                 : text {
                     label      = "&Justification" ;
                     width      = 10 ;
                     fixed_width= true ;
                 }
                 spacer_1 ;
                 : popup_list {
                     key        = "ID_PLIST_JUST";
                     mnemonic   = "J" ;
                     width  = 25 ;
                     fixed_width= true ;
                 }
             }
             : row {
                 : text {
                     label      = "Text &Style:";
                     width      = 15 ;
                     fixed_width= true ;
                 }
                 spacer_1 ;
                 : popup_list {
                     key        = "ID_PLIST_STYLE";
                     mnemonic   = "S" ;
                     width      = 25 ;
                     fixed_width= true ;
                 }
             }
             : row {
                 : button {
                     key    = "ID_BUTT_HEIGHT" ;
                     label  = "H&eight <" ;
                     width  = 15 ;
                     fixed_width= true ;
                 }
                 : edit_box {
                     key    = "ID_EBOX_HEIGHT" ;
                     width  = 25 ;
                     fixed_width= true ;
                 }
             }
             : row {
                 : button {
                     key    = "ID_BUTT_ROTATE" ;
                     label  = "&Rotation <" ;
                     width  = 15 ;
                     fixed_width= true ;
                 }
                 : edit_box {
                     key    = "ID_EBOX_ROTATE" ;
                     width  = 25 ;
                     fixed_width= true ;
                 }
             }
        }
    }
    ase_ok_cancel_help ;
    ase_errtile;
}

//---------------------------------------------------------------------
//
//  ASELINK dialogue box
//
aselinks : dialog {
    aspect_ratio = 0;
    label = "Links";
    : boxed_row {
         label                = "Database Ob&ject Filters";
         children_fixed_width = true ;
         env_cat_sch_tab_lpn;
    }
    : column {
        : row {
            : column {
                : text {
                    label         = "Link Path Name:";
                    width         = 17 ;
                    alignment     = left ;
                    fixed_width   = true ;
                }
                : text {
                    label         = "Table Path:";
                    width         = 17 ;
                    alignment     = left ;
                    fixed_width   = true ;
                }
            }
            : column {
                : text {
                    key           = ID_TEXT_PNAME ;
                    width         = 40 ;
                    alignment     = left ;
                    fixed_width   = true ;
                }
                : text {
                    key           = ID_TEXT_PATH ;
                    width         = 40 ;
                    alignment     = left ;
                    fixed_width   = true ;
                }
            }
        }
        : row {
            : column {
                : concatenation {
                    : text {
                         key        = "ID_TEXT_COLLABEL";
                         mnemonic   = "s" ;
                         width      = 22 ;
                         alignment  = left ;
                         fixed_width= true;
                    }
                    : text {
                         label      = "Link: #";
                         width      = 8 ;
                         alignment = left ;
                         fixed_width = true;
                    }
                    : text {
                         key        = "ID_TEXT_LNUMB";
                         width      = 4;
                         alignment = left ;
                         fixed_width = true;
                    }
                    : text {
                         label      = " of ";
                         width      = 4;
                         alignment = left ;
                         fixed_width = true;
                    }
                    : text {
                         key        = "ID_TEXT_LTOTAL";
                         width      = 4;
                         fixed_width = true;
                         alignment = left ;
                    }
                }
                : list_box {
                    alignment   = left ;
                    key         = "ID_LBOX_KEYCOL";
                    width       = 47 ;
                    height      = 4 ;
                    fixed_width = true ;
                    fixed_height= true ;
                    tabs        = "20 25 30 35" ;
                    tab_truncate= true ;
                    fixed_width_font = true ;
                }
                : slider {
                    key         = "ID_SLIDER";
                    layout      = horizontal ;
                    min_value   = 0;
                    max_value   = 255;
                    width       = 47 ;
                    fixed_width = true ;
                }
            }
            : column {
                children_alignment = centered ;
                ase_next_button ;
                ase_prior_button ;
                ase_first_button ;
                ase_last_button ;
            }
            : column {
               : toggle {
                   key          = "ID_TOGG_DACOLS" ;
                   label        = "DA &Columns" ;
               }
               : boxed_column {
                   label        = /*MSG*/"Selected &Object" ;
                   : toggle {
                        key     = "ID_TOGG_SUM";
                        label   = "Nested Links";
                   }
                   : popup_list {
                       width    = 12;
                       key      = "ID_PLIST_SELENT";
                   }
               }
            }
        }
        : column {
            children_fixed_width = true ;
            : row {
                : text {
                    label       = "Name: ";
                    width       = 6 ;
                    fixed_width = true ;
                }
                : text {
                    alignment   = left ;
                    key         = "ID_TEXT_COLUMN" ;
                    width       = 67 ;
                }
            }
            : row {
                : edit_box {
                    alignment   = left ;
                    label       = "&Value: ";
                    key         = "ID_EBOX_KVAL";
                    edit_width  = 67 ;
                }
            }
        }
        : row {
            children_fixed_width = true ;
            spacer_1 ;
            : ase_hlight_button { width = 10 ;}
            ase_upd_button;
            : button {
                 key       = "ID_BUTT_ROWS";
                 label     = "&Rows...";
                 width     = 10 ;
            }
            : ase_delete_button { width = 10 ;}
            : button {
                 key       = "ID_BUTT_DELALL";
                 label     = "Delete &All";
                 width     = 10 ;
            }
            spacer_1 ;
        }
    }
    ase_ok_cancel_help;
    ase_errtile;
}

//---------------------------------------------------------------------
//
//  ASESELECT dialogue box
//
aseselect : dialog {
   aspect_ratio = 0;
   label = "Select Objects";
   : boxed_row {
       label                = "Database Ob&ject Filters";
       children_fixed_width = true ;
       env_cat_sch_tab_lpn;
   }
   : boxed_column {
       label              = "Selection Set";
       alignment          = left ;
       : row {
           : ase_graph_button { width = 13 ; fixed_width = true ; }
       }
       : row {
           : button {
               label       = "&SELECT";
               key         = "ID_BUTT_SELECT";
               width       = 13 ;
               fixed_width = true ;
           }
           : edit_box {
               edit_width  = 48 ;
               fixed_width = true ;
               key         = "ID_EBOX_COND";
               label       = "&Condition:";
               edit_limit     = 256 ;
           }
       }
   }
   : boxed_row {
       label           = "Logical operations";
       children_fixed_width = true ;
       : button {
           key      = "ID_BUTT_UNION";
           label    = "&Union";
           width    = 15 ;
       }
       : button {
           key      = "ID_BUTT_SUBTRB";
           label    = "Subtract A-&B";
           width    = 15 ;
       }
       : button {
           key      = "ID_BUTT_SUBTRA";
           label    = "Subtract B-&A";
           width    = 15 ;
       }
       : button {
           key      = "ID_BUTT_INTER";
           label    = "&Intersect";
           width    = 15 ;
       }
   }
   : column {
       children_alignment   = left ;
       children_fixed_width = true;
       : row {
           : text {
               label       = "Selected Objects : ";
               alignment   = left ;
           }
           : text {
               key         = "ID_TEXT_LENGTH";
               width       = 10;
               alignment   = left ;
           }
           : spacer {width = 25 ; }
       }
   }
   ase_ok_cancel_help;
   ase_errtile;
}

//---------------------------------------------------------------------
//
//  ASEEXPORT dialogue box
//
aseexport : dialog {
    aspect_ratio = 0;
    label = "Export Links";
    : column {
        : boxed_row {
            label               = "Database Ob&ject Filters";
            children_fixed_width= true ;
            env_cat_sch_tab_lpn;
        }
        : boxed_row {
            label               = "Export Assignment";
            children_fixed_width= true ;
            : column {
               : concatenation {
                   : text {
                       width       = 31 ;
                       fixed_width = true ;
                       label       = "Source &LPN:";
                   }
                   : text {
                       fixed_width = true ;
                       label       = "Format:";
                       width       = 10 ;
                   }
                   : text {
                       label       = "Target:";
                   }
               }
               : list_box {
                   fixed_width_font = true ;
                   alignment       = left ;
                   key             = "ID_LBOX_TABLES";
                   mnemonic        = "L" ;
                   multiple_select = true ;
                   height          = 6 ;
                   width           = 73 ;
                   tabs            = "33 44 56" ;
                   tab_truncate    = true ;
               }
               : slider {
                   key             = "ID_SLIDER";
                   layout          = horizontal ;
                   min_value       = 0;
                   max_value       = 255;
                   width           = 73;
               }
               : row {
                   children_alignment = left ;
                   : text {
                       label       = "Selected Links: ";
                       fixed_width = true ;
                   }
                   : text {
                       key         = "ID_TEXT_SELNUMB";
                       width       = 8 ;
                       fixed_width = true ;
                   }
                   : spacer {
                      width        = 49 ;
                      fixed_width = true ;
                   }
               }

               : row {
                   children_alignment   = left ;
                   children_fixed_width = true ;
                   : column {
                       alignment        = left ;
                       : text {
                           label        = "&Format:" ;
                           key          = "ID_TEXT_FORMAT" ;
                       }
                       : popup_list {
                           width        = 12 ;
                           key          = "ID_PLIST_ASSIGN" ;
                           mnemonic     = "F" ;
                       }
                   }
                   : column {
                       alignment        = left ;
                       : text {
                           label        = "Tar&get:";
                           key          = "ID_TEXT_TARGET";
                       }
                       : edit_box {
                           key          = "ID_EBOX_TARGET";
                           mnemonic     = "g";
                           width        = 32 ;
                       }
                   }
                   : column {
                       alignment        = left ;
                       : spacer {
                           height       = 1 ;
                       }
                       : button {
                           label        = "&Save As...";
                           key          = "ID_BUTT_FILE";
                           width        = 12 ;
                       }
                   }
                   : column {
                       alignment        = left ;
                       : spacer {
                           height       = 1 ;
                       }
                       : button {
                           key          = "ID_BUTT_ASSIGN";
                           label        = "&Assign";
                           width        = 12 ;
                       }
                   }
               }
           }
       }
   }
   : row {
       alignment = centered ;
       fixed_width = true ;
       : spacer { width = 3 ; }
       : button {
           key        = "ID_BUTT_EXPORT";
           label      = "&Export";
       }
       : spacer { width = 1 ; }
       ase_close_button ;
       : spacer { width = 1 ; }
       ase_help_button ;
       : spacer { width = 3 ; }
   }
   ase_errtile;
}

//---------------------------------------------------------------------
//
//  ASESQLED dialogue box
//
asesqled : dialog {
    aspect_ratio = 0;
    label = "SQL Editor";
    initial_focus = "ID_EBOX_STM";
    : row {
       : column {
           : boxed_row {
               label      = "Database Object Settings";
               children_fixed_width = true ;
               : column {
                   alignment         = left ;
                   : text {
                     label           = "En&vironment";
                     width           = 18 ;
                   }
                   : popup_list {
                     width           = 18 ;
                     mnemonic        = "v" ;
                     key             = "ID_PLIST_ENV";
                   }
               }
               : column {
                   : text {
                     label           = "Cata&log";
                     width           = 18 ;
                   }
                   : popup_list {
                     width           = 18 ;
                     key             = "ID_PLIST_CAT";
                     mnemonic        = "l" ;
                   }
               }
               : column {
                   : text {
                     label           = "Sche&ma";
                     width           = 18 ;
                   }
                   : popup_list {
                     width           = 18 ;
                     key             = "ID_PLIST_SCH";
                     mnemonic        = "m" ;
                   }
               }
           }
           : list_box {
               label              = "Histor&y";
               key                = "ID_LBOX_HISTORY";
               height             = 4 ;
               fixed_width_font   = true ;
           }
           : slider {
               key                = "ID_SLIDER" ;
               layout             = horizontal ;
               min_value          = 0 ;
               max_value          = 255 ;
           }
       }
       : column {
           : boxed_row {
               label   = "Transaction Mode" ;
               : radio_column {
                   : radio_button {
                       key        = "ID_RBUT_RDONL";
                       label      = "&Read-only";
                   }
                   : radio_button {
                       key        = "ID_RBUT_RWR";
                       label      = "Read-&write";
                   }
               }
           }
           : boxed_row {
               label          = "Cursor State";
               : toggle {
                   label      = "&Scrollable";
                   key        = "ID_TOGG_SCROL";
               }
           }
           : column {
               iso_level_list ;
           }
       }
   }
   : boxed_column {
       label                   = "SQL Statement";
       : edit_box {
           key            = "ID_EBOX_STM";
           label          = "S&QL:" ;
           allow_accept   = true ;
           edit_limit     = 256 ;
       }
       : row {
            : toggle {
                label          = "&Autocommit";
                key            = "ID_TOGG_AUTO";
                alignment      = left ;
            }
            : toggle {
                alignment      = left ;
                label          = "&Native";
                key            = "ID_TOGG_NTV";
                alignment      = left ;
            }
            : ase_execute_button {
                alignment      = centered ;
                width          = 15 ;
                fixed_width    = true ;
            }
            : button {
                label          = "&File...";
                key            = "ID_BUTT_FILE";
                alignment      = right ;
                width          = 15 ;
                fixed_width    = true ;
            }
       }
    }
    : row {
        fixed_width     = true;
        alignment      = centered ;
        : button {
             label      = "C&ommit" ;
             key        = "ID_BUTT_COMMIT";
        }
        : spacer { width = 2; }
        : button {
             label      = "Roll&back" ;
             key        = "ID_BUTT_RBACK";
        }
        : spacer { width = 2; }
        ase_close_button ;
        : spacer { width = 2; }
        ase_help_button ;
    }
    ase_errtile;
}


//---------------------------------------------------------------------
//
//  SQL Cursor dialogue box
//
cursor : dialog {
    aspect_ratio = 0;
    label = "SQL Cursor";
    : column {
        ase_sql_statement ;
        : column {
             : row {
                 : column {
                     children_fixed_width = true ;
                     : list_box {
                         alignment      = left ;
                         key            = "ID_LBOX_CURSOR";
                         width          = 60 ;
                         height         = 7 ;
                         tabs           = "20 25 30 35" ;
                         tab_truncate   = true ;
                         fixed_width_font = true ;
                     }
                     : slider {
                         key            = "ID_SLIDER";
                         layout         = horizontal ;
                         min_value      = 0;
                         max_value      = 255;
                         width          = 60 ;
                     }
                 }
                 : column {
                     fixed_width = true ;
                     ase_next_button ;
                     ase_prior_button ;
                     ase_first_button ;
                     ase_last_button ;
                 }
            }
            : column {
               children_fixed_width = true ;
               : row {
                   : text {
                       label       = "Name: ";
                       width       = 6 ;
                       fixed_width = true ;
                   }
                   : text {
                       alignment   = left ;
                       key         = "ID_TEXT_COLUMN" ;
                       width       = 62 ;
                   }
               }
               : row {
                   : edit_box {
                       alignment   = left ;
                       label       = "&Value: ";
                       key         = "ID_EBOX_KVAL";
                       edit_width  = 62 ;
                   }
               }
            }
            : row {
                 alignment = centered ;
                 : spacer { width = 8; }
                 ase_upd_button ;
                 : spacer { width = 1; }
                 ase_delete_button;
                 : spacer { width = 1; }
                 ase_close_button;
                 : spacer { width = 1; }
                 ase_help_button;
                 : spacer { width = 8; }
            }
        }
    }
    ase_errtile;
}

//---------------------------------------------------------------------
//
//  SQL Syntax Error dialogue box
//
warning : dialog {
    aspect_ratio = 0;
    label = "ASE Warning" ;
    : column {
        children_fixed_height = true ;
        : column {
            : concatenation {
                children_alignment = left ;
                : text {
                     label  = "Error List:  Error ";
                }
                : text {
                     key    = "ID_TEXT_ERRNUM";
                     width  = 7 ;
                }
                : text {
                     label  = " of    ";
                }
                : text {
                     key    = "ID_TEXT_ERRTOTAL";
                     width  = 7 ;
                }
            }
            : list_box {
                key         = "ID_LBOX_SQLERR" ;
                height      = 4 ;
                fixed_width_font= true ;
            }
            : slider {
                key         = "ID_SLDR_ERRMSG" ;
                layout      = horizontal ;
                min_value   = 0;
                max_value   = 255;
            }
            : list_box {
                label       = "Diagnostic Parameters: ";
                key         = "ID_LBOX_ASEERR" ;
                tabs        = "30 35 40" ;
                tab_truncate   = true ;
                height      = 3 ;
                fixed_width_font= true ;
            }
            : slider {
                key         = "ID_SLDR_EXTEND" ;
                layout      = horizontal ;
                min_value   = 0;
                max_value   = 255;
            }
        }
        : boxed_column {
            label       = "SQL Statement" ;
            : row {
                children_fixed_width  = true ;
                alignment = centered ;
                : column {
                    alignment = left ;
                    : text {
                        label = "Left character position: " ;
                    }
                    : edit_box {
                        width       = 3 ;
                        key         = "ID_EBOX_SPOS" ;
                        fixed_width  = true ;
                    }
                }
                : column {
                    alignment = centered ;
                    spacer_1 ;
                    : row {
                         : text {
                             label = "Error Position: " ;
                         }
                         : text {
                             key   = "ID_TEXT_ERRPOS" ;
                             width = 3 ;
                             fixed_width  = true ;
                         }
                    }
                }
                : spacer {
                    width   = 15 ;
                }
            }
            : text {
                key         = "ID_TEXT_ERRMSG" ;
                alignment   = left ;
                width       = 50 ;
                fixed_width = true ;
            }
            : slider {
                key         = "ID_SLIDER" ;
                layout      = horizontal ;
                min_value   = 0;
                max_value   = 255;
            }
        }
        : row {
            alignment   = centered ;
            : spacer { width = 10 ;}
            : ase_close_button {is_default = true ;}
            : spacer { width = 10; }
        }
    }
}

ase_none_button : retirement_button {
    label          = "&None";
    key            = "ID_BUTT_NONE";
    width          = 10 ;
}

//---------------------------------------------------------------------
//
//  MLPN dialogue box
//
mlpn : dialog {
    aspect_ratio = 0;
    label = "Select Primary LPN" ;
    : list_box {
        alignment = left ;
        label     = "Defined LPNs With Same Key Column(s)" ;
        width     = 50 ;
        height    = 8 ;
        fixed_width_font = true ;
        key       = "ID_LBOX_LPNS";
        mnemonic  = "D" ;
    }
    : boxed_row {
        label     = "Selected Link Path Name";
        : text {
            key   = "ID_TEXT_CURRSET" ;
        }
    }
    : row {
         : spacer {width = 10 ;}
         ase_select_button;
         ase_none_button;
         ase_cancel_button;
         ase_help_button;
         : spacer {width = 10 ;}
    }
    ase_errtile;
}

//---------------------------------------------------------------------
//
//  MLPN erase confirmation dialogue box
//
mlpnerase : dialog {
    aspect_ratio = 0;
    label = "Primary LPN Erase Confirmation" ;
    : column {
       children_fixed_width   = true ;
       children_alignment     = centered ;
       : column {
            : text {
                  alignment   = centered ;
                  width       = 60 ;
                  key         = "ID_TEXT_ERASEPLPN";
                  value       = "Primary Link Path Name";
            }
            : text {
                  alignment   = centered ;
                  key         = "ID_TEXT_OBJECT";
                  width       = 55 ;
            }
            : text {
                  alignment   = centered ;
                  key         = "ID_TEXT_MSG";
                  width       = 55 ;
            }
            : text {
                  alignment    = centered ;
                  key          = "ID_TEXT_ERASEMLPN";
                  width        = 55 ;
                  value       =
                  "Erase Link Path Name with related link(s)";
            }
            : text {
                  alignment   = centered ;
                  width       = 60 ;
                  key         = "ID_TEXT_ERASESLPN";
                  value       =
                  "and Secondary LPN(s) with subordinate link(s)?";
            }
        }
    }
    : concatenation {
        children_fixed_width = true ;
        : text {
             label      = "Secondary LPN(s)";
             width      = 32 ;
        }
        : text {
             width      = 5 ;
             label      = "links";
        }
    }
    : list_box {
        alignment = left ;
        width     = 40 ;
        height    = 8 ;
        tabs            = "32" ;
        tab_truncate    = true ;
        fixed_width_font = true ;
        key       = "ID_LBOX_ESLPNS";
    }
    : row {
         : spacer {width = 10 ;}
         ase_ok_button;
         ase_cancel_button;
         ase_help_button;
         : spacer {width = 10 ;}
    }
    ase_errtile;
}
