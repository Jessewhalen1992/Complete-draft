// Next available MSG number is   341 
// MODULE_ID RENDCOMM_DCL_
// $Header: //streams/main/develop/global/src/coreapps/render/dcl/rendcomm.dcl#1 $ $Change: 360915 $ $DateTime: 2013/02/11 08:55:58 $ $Author: ongk $

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
//.

//***************************************************************************
//
// Common Render Dialogue Control Language (DCL) -- Version 1.0
//
//***************************************************************************

//***************************************************************************
// Black Cat dialog boxes
//***************************************************************************
//3DS input
object_list : list_box
{
    label   = "Object Name:          Type:";
    tabs    = "18";
    height  = 7;
    width   = 28;
    multiple_select = true;
}


//***************************************************************************
//
//***************************************************************************
// this is a clone of ok_cancel_help, without a default button
okNoDef_cancel_help : column 
{
    : row 
    {
        fixed_width = true;
        alignment = centered;
        : ok_button 
        {
            is_default      = false;
        }
        : spacer { width = 2; }
        cancel_button;
        : spacer { width = 2; }
        help_button;
    }
}
//***************************************************************************
//
//***************************************************************************
 
bc3dsin : dialog
{
    label   = "3D Studio File Import Options";

    : column {
        : row {
            : column {
                : boxed_column {
                    label           = "Available Objects";
                    : object_list {
                        key     = "available";
                    }
                    : row {
                        children_fixed_width    = true;

                        spacer_1;
                        : button {
                            key     = "selall";
                            label   = "Add All";
                            mnemonic = "A";
                            /*is_default = true;*/
                        }
                        : button {
                            key     = "select";
                            label   = "Add";
                            mnemonic = "d";
                        }
                        spacer_1;
                    }
                }
                spacer_1;
                : boxed_radio_column
                {
                    label           = "Save to Layers:";
                    : radio_button
                    {
                        key     = "byobject";
                        label   = "By Object";
                        mnemonic = "O";
                    }
                    : radio_button
                    {
                        key     = "bymaterial";
                        label   = "By Material";
                        mnemonic = "M";
                    }
                    : radio_button
                    {
                        key     = "bycolor";
                        label   = "By Object Color";
                        mnemonic = "B";
                    }
                    : radio_button
                    {
                        key     = "onelayer";
                        label   = "Single Layer";
                        mnemonic = "L";
                    }
                }
            }
            spacer_1;
            : column
            {
                : boxed_column {       
                    label           = "Selected Objects";
                    : object_list {
                        key     = "selected";
                    }
                    : row
                    {
                        children_fixed_width    = true;
                
                        spacer_1;
                        : button
                        {
                            key     = "remove";
                            label   = "Remove";
                            mnemonic = "R";
                        }
                        : button
                        {
                            key     = "rmvall";
                            label   = "Remove All";
                            mnemonic = "v";
                        }
                        spacer_1;
                    }
                }
                spacer_1;
                : boxed_radio_column
                {
                    label           = "Multiple Material Objects:";
                    : radio_button
                    {
                        key     = "prompt";
                        label   = "Always Prompt";
                        mnemonic = "P";
                    }
                    : radio_button
                    {
                        key     = "break";
                        label   = "Split by Material";
                        mnemonic = "S";
                    }
                    : radio_button
                    {
                        key     = "first";
                        label   = "Assign First Material";
                        mnemonic = "F";
                    }
                    : radio_button
                    {
                        key     = "none";
                        label   = "Don't Assign a Material";
                        mnemonic = "n";
                    }
                }
            }
        }
        spacer_1;
        okNoDef_cancel_help;
    }
}


//***************************************************************************
//
//***************************************************************************
bcmatls : dialog
{
    label   = "Material Assignment Alert";
    
    : text {
        key     = "objname";
        width   = 50; /* "Object1234567890 has multiple materials assigned" */
    }
    : boxed_row {
        label                   = "";
        children_alignment      = left;
        children_fixed_width    = true;

        : column { width = 11; }
        : column {

            : radio_column {
                : radio_button {
                    key     = "breakapart";
                    label   = "Split Object By Material";
                    mnemonic = "S";
                }
                : radio_button {
                    key     = "applyfirst";
                    label   = "Assign First Material";
                    mnemonic = "A";
                }
                : radio_button {
                    key     = "applyone";
                    label   = "Select a Material:";
                    mnemonic = "M";
                }
            }
            : row {
                : popup_list {
                    key     = "materials";
                    value   = "0";
                    edit_width  = 25;
                }
            }
        }
        : column { width = 11; }
    }
    spacer_1;
    ok_cancel_help;
}
