/* 
	Copyright 1988-2000 by Autodesk, Inc.

	Permission to use, copy, modify, and distribute this software
	for any purpose and without fee is hereby granted, provided
	that the above copyright notice appears in all copies and
	that both that copyright notice and the limited warranty and
	restricted rights notice below appear in all supporting
	documentation.

	AUTODESK PROVIDES THIS PROGRAM "AS IS" AND WITH ALL FAULTS.
	AUTODESK SPECIFICALLY DISCLAIMS ANY IMPLIED WARRANTY OF
	MERCHANTABILITY OR FITNESS FOR A PARTICULAR USE.  AUTODESK, INC.
	DOES NOT WARRANT THAT THE OPERATION OF THE PROGRAM WILL BE
	UNINTERRUPTED OR ERROR FREE.

	Use, duplication, or disclosure by the U.S. Government is subject to
	restrictions set forth in FAR 52.227-19 (Commercial Computer
	Software - Restricted Rights) and DFAR 252.227-7013(c)(1)(ii) 
	(Rights in Technical Data and Computer Software), as applicable.

    Autodesk Publish to Web JavaScript 
    
    Template element id: adsk_ptw_list_of_drawings
    Publishing content:  label

    Template element id: adsk_ptw_image_description
    Publishing content:  description

    Template element id: adsk_ptw_image
    Publishing content:  image

    Template element id: adsk_ptw_idrop
    Publishing content:  idrop

    Template element id: adsk_ptw_summary_frame
    Publishing content:  drawing summary    
*/

adsk_ptw_list_of_drawings_main();

function adsk_ptw_list_of_drawings_main() {
    var xmle=adsk_ptw_xml.getElementsByTagName("publish_to_web").item(0);
    xmle=xmle.getElementsByTagName("contents").item(0);
    var xmles=xmle.getElementsByTagName("content");
    var e = document.getElementById("adsk_ptw_list_of_drawings");

    table = document.createElement("table");
    tbody = document.createElement("tbody");
    e.appendChild(table);
    table.appendChild(tbody);
    tr = document.createElement("tr");
    tbody.appendChild(tr);
    tr.align="left";
    tr.vAlign="top"

    td = document.createElement("td"); 
    tr.appendChild(td);
    table2=document.createElement("table"); 
    td.appendChild(table2);
    table2.cellPadding=1;
    table2.cellSpacing=5;

    tbody2=document.createElement("tbody");
    table2.appendChild(tbody2);
    td2 = document.createElement("td"); 
    tr.appendChild(td2);
    for (i=0; i < xmles.length; i++) {
        content=xmles.item(i);      
        title = content.getElementsByTagName("title").item(0);
        a=document.createElement("a"); 
        if (null == title.firstChild) {
            a.appendChild(document.createTextNode(" "));
        }
        else {
            a.appendChild(document.createTextNode(title.firstChild.data));
        }
        table2_tr=document.createElement("tr");
        tbody2.appendChild(table2_tr);
        table2_td=document.createElement("td");
        table2_tr.appendChild(table2_td);
        table2_td.appendChild(a);
        a.className = "DRAWING_LABEL";
        var imgs = content.getElementsByTagName("imagex");
        var img = imgs.item(0);
        var fileName = img.firstChild.data;
        a.id=fileName;
        a.value=i;   
        if (adsk_ptw_list_of_drawings_is_image_dwf(fileName)) {  
            a.href="javascript:adsk_ptw_list_of_drawings_onClickViewer()";
        } else {
            a.href="javascript:adsk_ptw_list_of_drawings_onClickImage()";
        }
    }
}

function adsk_ptw_list_of_drawings_createViewerControl (i) {
    dwg_img_desc=parent.adsk_ptw_image_frame.document.getElementById("adsk_ptw_image_description");
	if (null != dwg_img_desc.firstChild)
		dwg_img_desc.removeChild(dwg_img_desc.firstChild);

    var xmle=adsk_ptw_xml.getElementsByTagName("publish_to_web").item(0);
    xmle=xmle.getElementsByTagName("contents").item(0);
    xmles=xmle.getElementsByTagName("content");
    desc = xmles.item(i).getElementsByTagName("description").item(0);
    p = parent.adsk_ptw_image_frame.document.createElement("p");
    if (null == desc.firstChild) {
        p.appendChild(parent.adsk_ptw_image_frame.document.createTextNode(""));
    }
    else {
        p.appendChild(parent.adsk_ptw_image_frame.document.createTextNode(desc.firstChild.data));
    }
    dwg_img_desc.appendChild(p);

    dwg_img=parent.adsk_ptw_image_frame.document.getElementById("adsk_ptw_image");
    if (null != dwg_img_desc.firstChild)
		dwg_img.removeChild(dwg_img.firstChild); 

    viewerInstalled = false;
	IE4plus = (document.all) ? true : false;
	if (IE4plus)
	    // try and catch work fine in IE, but will generate errors in Netscape. So evaluating try and catch block as string and evaluate it usung eval fuction
  	    eval ('try {var xObj = new ActiveXObject("AdView.Adviewer");if (xObj == null) viewerInstalled = false; else viewerInstalled = true; } catch (e) { viewerInstalled = false; }');

    activex = parent.adsk_ptw_image_frame.document.createElement("object");
    dwg_img.appendChild(activex);
    activex.classid="clsid:A662DA7E-CCB7-4743-B71A-D817F6D575DF";

    var fileName = xmles.item(i).getElementsByTagName("imagex").item(0).firstChild.data;
    activex.id="AdView";
    activex.SourcePath=fileName;
    if (viewerInstalled && activex.Viewer && activex.Viewer.ToolbarVisible)
        activex.Viewer.ToolbarVisible=false;
    activex.border="1";
    activex.width="500";
    activex.height="360";

	if (!viewerInstalled)
		adsk_ptw_onerror(); // Redirects the user to a website where they can download Autodest Express Viewer

    adsk_ptw_list_of_drawings_setiDrop(i);
}

function adsk_ptw_list_of_drawings_onClickViewer() { 
    adsk_ptw_list_of_drawings_createViewerControl (document.activeElement.value);
    adsk_ptw_list_of_drawings_set_summary_info(document.activeElement.value);
}

function adsk_ptw_list_of_drawings_createImageElement(i) {
    dwg_img_desc=parent.adsk_ptw_image_frame.document.getElementById("adsk_ptw_image_description");
    if (null != dwg_img_desc.firstChild)
		dwg_img_desc.removeChild(dwg_img_desc.firstChild);

    var xmle=adsk_ptw_xml.getElementsByTagName("publish_to_web").item(0);
    xmle=xmle.getElementsByTagName("contents").item(0);
    var xmles=xmle.getElementsByTagName("content");
    desc = xmles.item(i).getElementsByTagName("description").item(0);
    p = parent.adsk_ptw_image_frame.document.createElement("p");
    if (null == desc.firstChild) {
        p.appendChild(parent.adsk_ptw_image_frame.document.createTextNode(""));
    }
    else {
        p.appendChild(parent.adsk_ptw_image_frame.document.createTextNode(desc.firstChild.data));
    }
    dwg_img_desc.appendChild(p);

    dwg_img=parent.adsk_ptw_image_frame.document.getElementById("adsk_ptw_image");
	dwg_img.removeChild(dwg_img.firstChild); 
    image = parent.adsk_ptw_image_frame.document.createElement("img");
    dwg_img.appendChild(image);
    var fstItem = xmles.item(i).getElementsByTagName("imagex").item(0);
    var fstChild = fstItem.firstChild;
    image.src = fstChild.data;
    image.border=1;

    adsk_ptw_list_of_drawings_setiDrop(i);
}

function adsk_ptw_list_of_drawings_onClickImage() {
    adsk_ptw_list_of_drawings_createImageElement(document.activeElement.value);
    adsk_ptw_list_of_drawings_set_summary_info(document.activeElement.value);
}

function adsk_ptw_list_of_drawings_setiDrop(i) {
    dwg_idrop = parent.adsk_ptw_image_frame.document.getElementById("adsk_ptw_idrop");
    if (null != dwg_idrop.firstChild) {
	    dwg_idrop.removeChild(dwg_idrop.firstChild); 
    }
    var xmle=adsk_ptw_xml.getElementsByTagName("publish_to_web").item(0);
    xmle=xmle.getElementsByTagName("contents").item(0);
    var xmles=xmle.getElementsByTagName("content");
    idrop = xmles.item(i).getElementsByTagName("iDropXML").item(0);
    if (null != idrop.firstChild) {
        activex = parent.adsk_ptw_image_frame.document.createElement("object");
        dwg_idrop.appendChild(activex);
        activex.codeBase=xmsg_adsk_ptw_all_idrop_url;
        activex.classid="clsid:21E0CB95-1198-4945-A3D2-4BF804295F78";
        activex.package=idrop.firstChild.data;
        activex.background="iDropButton.gif";
        activex.width="16";
        activex.height="16";
    }
}

function adsk_ptw_list_of_drawings_set_summary_info(i) {
    if (null == parent) return;
    if (null == parent.adsk_ptw_summary_frame) return;

    body=parent.adsk_ptw_summary_frame.document.getElementsByTagName("body").item(0);
    n=body.childNodes.length;
    for (index=0; index < n; index++) {
        body.removeChild(body.firstChild);
    }

    var xmle=adsk_ptw_xml.getElementsByTagName("publish_to_web").item(0);
    xmle=xmle.getElementsByTagName("contents").item(0);
    var xmles=xmle.getElementsByTagName("content");
    sum_info = xmles.item(i).getElementsByTagName("summary_info").item(0);

    if (null != sum_info) {
        body=parent.adsk_ptw_summary_frame.document.getElementsByTagName("body").item(0);

        title=sum_info.getElementsByTagName("title").item(0);
        adsk_ptw_list_of_drawings_summary(body, xmsg_adsk_ptw_all_summaryTitle, title);

        subject=sum_info.getElementsByTagName("subject").item(0);
        adsk_ptw_list_of_drawings_summary(body, xmsg_adsk_ptw_all_summarySubject, subject);

        author=sum_info.getElementsByTagName("author").item(0);
        adsk_ptw_list_of_drawings_summary(body, xmsg_adsk_ptw_all_summaryAuthor, author);

        keywords=sum_info.getElementsByTagName("keywords").item(0);
        adsk_ptw_list_of_drawings_summary(body, xmsg_adsk_ptw_all_summaryKeywords, keywords);

        comments=sum_info.getElementsByTagName("comments").item(0);
        adsk_ptw_list_of_drawings_summary(body, xmsg_adsk_ptw_all_summaryComments, comments);
        
        hyperlink_base=sum_info.getElementsByTagName("hyperlink_base").item(0);
        adsk_ptw_list_of_drawings_summary(body, xmsg_adsk_ptw_all_summaryHyperlinkBase, hyperlink_base);
    }
}

function adsk_ptw_list_of_drawings_summary(rootNode, nameString, valueNode) {
    if (null == valueNode) return;
    if (null == valueNode.firstChild) return;

    b=parent.adsk_ptw_summary_frame.document.createElement("b");
    div=parent.adsk_ptw_summary_frame.document.createElement("div");
    rootNode.appendChild(div);
    div.appendChild(b);
    str = nameString + valueNode.firstChild.data;
    b.appendChild(parent.adsk_ptw_summary_frame.document.createTextNode(str));
}

function adsk_ptw_list_of_drawings_is_image_dwf(file_name) {
    var ext = file_name.substring(file_name.lastIndexOf('.') + 1, (file_name.length));
    return("dwf" == ext || "dwfx" == ext);
}


// SIG // Begin signature block
// SIG // MIIpKAYJKoZIhvcNAQcCoIIpGTCCKRUCAQExDzANBglg
// SIG // hkgBZQMEAgEFADB3BgorBgEEAYI3AgEEoGkwZzAyBgor
// SIG // BgEEAYI3AgEeMCQCAQEEEBDgyQbOONQRoqMAEEvTUJAC
// SIG // AQACAQACAQACAQACAQAwMTANBglghkgBZQMEAgEFAAQg
// SIG // vFd5DX+Vfm042+pJ++oUNkmBsNWt2RDtYgdM32L9X2qg
// SIG // gg4ZMIIGsDCCBJigAwIBAgIQCK1AsmDSnEyfXs2pvZOu
// SIG // 2TANBgkqhkiG9w0BAQwFADBiMQswCQYDVQQGEwJVUzEV
// SIG // MBMGA1UEChMMRGlnaUNlcnQgSW5jMRkwFwYDVQQLExB3
// SIG // d3cuZGlnaWNlcnQuY29tMSEwHwYDVQQDExhEaWdpQ2Vy
// SIG // dCBUcnVzdGVkIFJvb3QgRzQwHhcNMjEwNDI5MDAwMDAw
// SIG // WhcNMzYwNDI4MjM1OTU5WjBpMQswCQYDVQQGEwJVUzEX
// SIG // MBUGA1UEChMORGlnaUNlcnQsIEluYy4xQTA/BgNVBAMT
// SIG // OERpZ2lDZXJ0IFRydXN0ZWQgRzQgQ29kZSBTaWduaW5n
// SIG // IFJTQTQwOTYgU0hBMzg0IDIwMjEgQ0ExMIICIjANBgkq
// SIG // hkiG9w0BAQEFAAOCAg8AMIICCgKCAgEA1bQvQtAorXi3
// SIG // XdU5WRuxiEL1M4zrPYGXcMW7xIUmMJ+kjmjYXPXrNCQH
// SIG // 4UtP03hD9BfXHtr50tVnGlJPDqFX/IiZwZHMgQM+TXAk
// SIG // ZLON4gh9NH1MgFcSa0OamfLFOx/y78tHWhOmTLMBICXz
// SIG // ENOLsvsI8IrgnQnAZaf6mIBJNYc9URnokCF4RS6hnyzh
// SIG // GMIazMXuk0lwQjKP+8bqHPNlaJGiTUyCEUhSaN4QvRRX
// SIG // XegYE2XFf7JPhSxIpFaENdb5LpyqABXRN/4aBpTCfMjq
// SIG // GzLmysL0p6MDDnSlrzm2q2AS4+jWufcx4dyt5Big2MEj
// SIG // R0ezoQ9uo6ttmAaDG7dqZy3SvUQakhCBj7A7CdfHmzJa
// SIG // wv9qYFSLScGT7eG0XOBv6yb5jNWy+TgQ5urOkfW+0/tv
// SIG // k2E0XLyTRSiDNipmKF+wc86LJiUGsoPUXPYVGUztYuBe
// SIG // M/Lo6OwKp7ADK5GyNnm+960IHnWmZcy740hQ83eRGv7b
// SIG // UKJGyGFYmPV8AhY8gyitOYbs1LcNU9D4R+Z1MI3sMJN2
// SIG // FKZbS110YU0/EpF23r9Yy3IQKUHw1cVtJnZoEUETWJrc
// SIG // JisB9IlNWdt4z4FKPkBHX8mBUHOFECMhWWCKZFTBzCEa
// SIG // 6DgZfGYczXg4RTCZT/9jT0y7qg0IU0F8WD1Hs/q27Iwy
// SIG // CQLMbDwMVhECAwEAAaOCAVkwggFVMBIGA1UdEwEB/wQI
// SIG // MAYBAf8CAQAwHQYDVR0OBBYEFGg34Ou2O/hfEYb7/mF7
// SIG // CIhl9E5CMB8GA1UdIwQYMBaAFOzX44LScV1kTN8uZz/n
// SIG // upiuHA9PMA4GA1UdDwEB/wQEAwIBhjATBgNVHSUEDDAK
// SIG // BggrBgEFBQcDAzB3BggrBgEFBQcBAQRrMGkwJAYIKwYB
// SIG // BQUHMAGGGGh0dHA6Ly9vY3NwLmRpZ2ljZXJ0LmNvbTBB
// SIG // BggrBgEFBQcwAoY1aHR0cDovL2NhY2VydHMuZGlnaWNl
// SIG // cnQuY29tL0RpZ2lDZXJ0VHJ1c3RlZFJvb3RHNC5jcnQw
// SIG // QwYDVR0fBDwwOjA4oDagNIYyaHR0cDovL2NybDMuZGln
// SIG // aWNlcnQuY29tL0RpZ2lDZXJ0VHJ1c3RlZFJvb3RHNC5j
// SIG // cmwwHAYDVR0gBBUwEzAHBgVngQwBAzAIBgZngQwBBAEw
// SIG // DQYJKoZIhvcNAQEMBQADggIBADojRD2NCHbuj7w6mdNW
// SIG // 4AIapfhINPMstuZ0ZveUcrEAyq9sMCcTEp6QRJ9L/Z6j
// SIG // fCbVN7w6XUhtldU/SfQnuxaBRVD9nL22heB2fjdxyyL3
// SIG // WqqQz/WTauPrINHVUHmImoqKwba9oUgYftzYgBoRGRjN
// SIG // YZmBVvbJ43bnxOQbX0P4PpT/djk9ntSZz0rdKOtfJqGV
// SIG // WEjVGv7XJz/9kNF2ht0csGBc8w2o7uCJob054ThO2m67
// SIG // Np375SFTWsPK6Wrxoj7bQ7gzyE84FJKZ9d3OVG3ZXQIU
// SIG // H0AzfAPilbLCIXVzUstG2MQ0HKKlS43Nb3Y3LIU/Gs4m
// SIG // 6Ri+kAewQ3+ViCCCcPDMyu/9KTVcH4k4Vfc3iosJocsL
// SIG // 6TEa/y4ZXDlx4b6cpwoG1iZnt5LmTl/eeqxJzy6kdJKt
// SIG // 2zyknIYf48FWGysj/4+16oh7cGvmoLr9Oj9FpsToFpFS
// SIG // i0HASIRLlk2rREDjjfAVKM7t8RhWByovEMQMCGQ8M4+u
// SIG // KIw8y4+ICw2/O/TOHnuO77Xry7fwdxPm5yg/rBKupS8i
// SIG // bEH5glwVZsxsDsrFhsP2JjMMB0ug0wcCampAMEhLNKhR
// SIG // ILutG4UI4lkNbcoFUCvqShyepf2gpx8GdOfy1lKQ/a+F
// SIG // SCH5Vzu0nAPthkX0tGFuv2jiJmCG6sivqf6UHedjGzqG
// SIG // VnhOMIIHYTCCBUmgAwIBAgIQCH7hieqmtSvEO2+hkIpM
// SIG // XDANBgkqhkiG9w0BAQsFADBpMQswCQYDVQQGEwJVUzEX
// SIG // MBUGA1UEChMORGlnaUNlcnQsIEluYy4xQTA/BgNVBAMT
// SIG // OERpZ2lDZXJ0IFRydXN0ZWQgRzQgQ29kZSBTaWduaW5n
// SIG // IFJTQTQwOTYgU0hBMzg0IDIwMjEgQ0ExMB4XDTIzMDcx
// SIG // NzAwMDAwMFoXDTI0MDcxNjIzNTk1OVowaTELMAkGA1UE
// SIG // BhMCVVMxEzARBgNVBAgTCkNhbGlmb3JuaWExEzARBgNV
// SIG // BAcTClNhbiBSYWZhZWwxFzAVBgNVBAoTDkF1dG9kZXNr
// SIG // LCBJbmMuMRcwFQYDVQQDEw5BdXRvZGVzaywgSW5jLjCC
// SIG // AiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAKKc
// SIG // yDoWrxsL4jV3XIRZUqz//dBldginxx6FQA0LWptqdL0+
// SIG // FUS1mzcT9W2lcIXG+VQFguY9sgJcvYSTnQmbIchKptvw
// SIG // CaJQvOo6+mLEIFiWgP2RuF67L8jRB61Q1+gcN35eLLnK
// SIG // 4runGSVGP0mtLWYSL2ryuWu5Y8VYRfnZHWwa84bqh0UW
// SIG // IAmhArTWl6LqufLTqgNpQTOAfZk5oAkBpRCFkSgzP8hm
// SIG // 2adhANyLh0iFWalGMz4WqZXdMRdCAtpQ15eJsTDbFmnr
// SIG // QBZ4/noX6XXFm2t/RvGkYg82VMiHnul9xRLEurfW+EO+
// SIG // FEQ58X00ee5ZGrUgJZeqdaQranLnhkAMp1k8qdtPPG9h
// SIG // /hfmi+4oBt+u2MfyeQFS/JglunH8L80NLbrrOjvVVKZz
// SIG // UVrRU34knt7yzbQyK8ogqGSL9BPZPumP8rc77H6SvoSu
// SIG // btiqEw0Ru4vb9BvjyuhtbS50qlPBM1wRQyfa89ITDsP3
// SIG // gc83ziWCLFHcU0KQ4akG8PvqERTybmlq5do4QWU1Z6tK
// SIG // YNTd3v+fur+xrvzqFDlyCi1YKCA04bkfkLg6bupNEe4N
// SIG // sJKt0M1A+HDmadcIj0GIfnhvsmPsQQQ5nM6umQu67IUL
// SIG // ccUJtzC9IYhqk9iLDRKMGXr8LIV4EmyDaAItYiFe/ndj
// SIG // KSDMx9KDlUfGLD1KCpxxAgMBAAGjggIDMIIB/zAfBgNV
// SIG // HSMEGDAWgBRoN+Drtjv4XxGG+/5hewiIZfROQjAdBgNV
// SIG // HQ4EFgQUP+qYY98y1gz7twN8OhZPK8gWlmswDgYDVR0P
// SIG // AQH/BAQDAgeAMBMGA1UdJQQMMAoGCCsGAQUFBwMDMIG1
// SIG // BgNVHR8Ega0wgaowU6BRoE+GTWh0dHA6Ly9jcmwzLmRp
// SIG // Z2ljZXJ0LmNvbS9EaWdpQ2VydFRydXN0ZWRHNENvZGVT
// SIG // aWduaW5nUlNBNDA5NlNIQTM4NDIwMjFDQTEuY3JsMFOg
// SIG // UaBPhk1odHRwOi8vY3JsNC5kaWdpY2VydC5jb20vRGln
// SIG // aUNlcnRUcnVzdGVkRzRDb2RlU2lnbmluZ1JTQTQwOTZT
// SIG // SEEzODQyMDIxQ0ExLmNybDA+BgNVHSAENzA1MDMGBmeB
// SIG // DAEEATApMCcGCCsGAQUFBwIBFhtodHRwOi8vd3d3LmRp
// SIG // Z2ljZXJ0LmNvbS9DUFMwgZQGCCsGAQUFBwEBBIGHMIGE
// SIG // MCQGCCsGAQUFBzABhhhodHRwOi8vb2NzcC5kaWdpY2Vy
// SIG // dC5jb20wXAYIKwYBBQUHMAKGUGh0dHA6Ly9jYWNlcnRz
// SIG // LmRpZ2ljZXJ0LmNvbS9EaWdpQ2VydFRydXN0ZWRHNENv
// SIG // ZGVTaWduaW5nUlNBNDA5NlNIQTM4NDIwMjFDQTEuY3J0
// SIG // MAkGA1UdEwQCMAAwDQYJKoZIhvcNAQELBQADggIBADM5
// SIG // oKAJ/GLdOA6cwT5Z+wAM5//a/CRnOKdheKmGtwzqsD8Z
// SIG // R93gSG18BCgn255uAVwzAyCS9mi3AI8uRJlIM6dij/1q
// SIG // mmJFJ3Ji3vPj8s5sSOZ7zNbkcCDS2haFXSFfhYyNQUkU
// SIG // aZkNgg1pCIK2asuDx8i6HYXihNWVEw0AFZ4ovvVK8G5P
// SIG // crb3nPpRgUGXRTno5UaHnAaD/7u2FP4rKr9DlNxdZuM6
// SIG // nerlKL9i3888lm7VQQ8ptMMq9f1XeBq+BcJF2HbO7iaO
// SIG // ayFZKuf/Hn3XH8t+SMHEP2N+3ESLT/YxwSpMp0st2t+D
// SIG // cTd7TntatIbnZYqal4G3L17Pr/euG9rq2RehiS3eBFox
// SIG // wgBTI46iW0SpOyeYMYbECeoenlZziCjY8y/alYv+xyWT
// SIG // PSL3oV0qACfvjXyuG5VD4jCEjhHP08QA1dma9ROyDz0q
// SIG // OKuei8QaOiWrgq6LFAFaMYZHGw8jE3Us9Hk/cFUrhz3Q
// SIG // AyeDgza57Uatp2X/hO94Q89KxmwduXTnY+LD40miitsI
// SIG // srIR+v6M/exAPetl7oHp92C6n4r/ArtbVoH/YYjh10hi
// SIG // tZNq1xcwLx8MIIJh5W1fuVZDy3oJP1VfH3wlDjwFmIuN
// SIG // aleEDksdEsJ97OfTwXc1o9fT8OpBIiDpjj4UEd24JcPl
// SIG // FoGOc+mG0Qk3J4MlG/T9MYIaZzCCGmMCAQEwfTBpMQsw
// SIG // CQYDVQQGEwJVUzEXMBUGA1UEChMORGlnaUNlcnQsIElu
// SIG // Yy4xQTA/BgNVBAMTOERpZ2lDZXJ0IFRydXN0ZWQgRzQg
// SIG // Q29kZSBTaWduaW5nIFJTQTQwOTYgU0hBMzg0IDIwMjEg
// SIG // Q0ExAhAIfuGJ6qa1K8Q7b6GQikxcMA0GCWCGSAFlAwQC
// SIG // AQUAoHwwEAYKKwYBBAGCNwIBDDECMAAwGQYJKoZIhvcN
// SIG // AQkDMQwGCisGAQQBgjcCAQQwHAYKKwYBBAGCNwIBCzEO
// SIG // MAwGCisGAQQBgjcCARUwLwYJKoZIhvcNAQkEMSIEIEMN
// SIG // aJt+n9ADgCOfeWC3Kl9vYhqnlWiGCxpDiggQtVeRMA0G
// SIG // CSqGSIb3DQEBAQUABIICACk8TOlfOhBuigjKOUWN3uoC
// SIG // Kf8euYI6jeu9M+q9zFBY16kq9sV5cQ0BJzAFq1Mjhcq6
// SIG // 8ovDbCS9h3glDyHziJRGnhfE4ebzEckdsW16KRKosx11
// SIG // sGN87iJEdUZ5jrn6LFEyxQV23mF8VQCW6u8VDq588Ov/
// SIG // 5s2klqG2IRTC0uRb39w3PvGmy+3N/LtNgj1l2bYmYfQV
// SIG // KsQ0nopk/yh/q1SjAvHsMlL3Yjs0agloiSAB2Lo1r9g0
// SIG // 3mKdhmWi4ACuHrJdw/xbVDDm2ZSByZcahgCe+vikge+N
// SIG // 5dMH230IzyBqA53QLi6Rea7ljJIU4gbJZ0sVrvfdGM9f
// SIG // SguNgTNu99NCoUH1jjKQ2JdlROYkyJxs53LGOiEEjKUt
// SIG // oY3YukXfPBQ4eoHVRM09U19X0Tm05UmnKIIEL7luh05y
// SIG // tqNdr5VIFhsDsbEVHM8GdNOrXP/VFtMxx9mEU/24mOCf
// SIG // GPYX8bCouJ37BhoHIqnVS3Ta0QEN0I3/NVEOH5EqdlT4
// SIG // Bk5plHaMGl0x+HUyDOkggtwaAISmv2JAimx1i1/iJ9YA
// SIG // Umlfy+DKdRGYmcTdAfliyG/xpoYDrg+kIXwnTWa4BP4R
// SIG // A/6rI2AKzU8q1HLEcaA+PMBQfqMaLHJfiN3t+c41WwZm
// SIG // TKGKKY4l+wiiU40m8cmIjqd+jlklcypz9im1MvVNPm00
// SIG // oYIXPTCCFzkGCisGAQQBgjcDAwExghcpMIIXJQYJKoZI
// SIG // hvcNAQcCoIIXFjCCFxICAQMxDzANBglghkgBZQMEAgEF
// SIG // ADB3BgsqhkiG9w0BCRABBKBoBGYwZAIBAQYJYIZIAYb9
// SIG // bAcBMDEwDQYJYIZIAWUDBAIBBQAEIL2tOOdCdasDoRkb
// SIG // tuJdDJ/HW023Iri+N//FOYQbTFHaAhAuC32YjSj5TifA
// SIG // P5WR49IyGA8yMDIzMDgwMjAzMTE1OVqgghMHMIIGwDCC
// SIG // BKigAwIBAgIQDE1pckuU+jwqSj0pB4A9WjANBgkqhkiG
// SIG // 9w0BAQsFADBjMQswCQYDVQQGEwJVUzEXMBUGA1UEChMO
// SIG // RGlnaUNlcnQsIEluYy4xOzA5BgNVBAMTMkRpZ2lDZXJ0
// SIG // IFRydXN0ZWQgRzQgUlNBNDA5NiBTSEEyNTYgVGltZVN0
// SIG // YW1waW5nIENBMB4XDTIyMDkyMTAwMDAwMFoXDTMzMTEy
// SIG // MTIzNTk1OVowRjELMAkGA1UEBhMCVVMxETAPBgNVBAoT
// SIG // CERpZ2lDZXJ0MSQwIgYDVQQDExtEaWdpQ2VydCBUaW1l
// SIG // c3RhbXAgMjAyMiAtIDIwggIiMA0GCSqGSIb3DQEBAQUA
// SIG // A4ICDwAwggIKAoICAQDP7KUmOsap8mu7jcENmtuh6BSF
// SIG // dDMaJqzQHFUeHjZtvJJVDGH0nQl3PRWWCC9rZKT9BoMW
// SIG // 15GSOBwxApb7crGXOlWvM+xhiummKNuQY1y9iVPgOi2M
// SIG // h0KuJqTku3h4uXoW4VbGwLpkU7sqFudQSLuIaQyIxvG+
// SIG // 4C99O7HKU41Agx7ny3JJKB5MgB6FVueF7fJhvKo6B332
// SIG // q27lZt3iXPUv7Y3UTZWEaOOAy2p50dIQkUYp6z4m8rSM
// SIG // zUy5Zsi7qlA4DeWMlF0ZWr/1e0BubxaompyVR4aFeT4M
// SIG // XmaMGgokvpyq0py2909ueMQoP6McD1AGN7oI2TWmtR7a
// SIG // eFgdOej4TJEQln5N4d3CraV++C0bH+wrRhijGfY59/XB
// SIG // T3EuiQMRoku7mL/6T+R7Nu8GRORV/zbq5Xwx5/PCUsTm
// SIG // FntafqUlc9vAapkhLWPlWfVNL5AfJ7fSqxTlOGaHUQhr
// SIG // +1NDOdBk+lbP4PQK5hRtZHi7mP2Uw3Mh8y/CLiDXgazT
// SIG // 8QfU4b3ZXUtuMZQpi+ZBpGWUwFjl5S4pkKa3YWT62SBs
// SIG // GFFguqaBDwklU/G/O+mrBw5qBzliGcnWhX8T2Y15z2LF
// SIG // 7OF7ucxnEweawXjtxojIsG4yeccLWYONxu71LHx7jstk
// SIG // ifGxxLjnU15fVdJ9GSlZA076XepFcxyEftfO4tQ6dwID
// SIG // AQABo4IBizCCAYcwDgYDVR0PAQH/BAQDAgeAMAwGA1Ud
// SIG // EwEB/wQCMAAwFgYDVR0lAQH/BAwwCgYIKwYBBQUHAwgw
// SIG // IAYDVR0gBBkwFzAIBgZngQwBBAIwCwYJYIZIAYb9bAcB
// SIG // MB8GA1UdIwQYMBaAFLoW2W1NhS9zKXaaL3WMaiCPnshv
// SIG // MB0GA1UdDgQWBBRiit7QYfyPMRTtlwvNPSqUFN9SnDBa
// SIG // BgNVHR8EUzBRME+gTaBLhklodHRwOi8vY3JsMy5kaWdp
// SIG // Y2VydC5jb20vRGlnaUNlcnRUcnVzdGVkRzRSU0E0MDk2
// SIG // U0hBMjU2VGltZVN0YW1waW5nQ0EuY3JsMIGQBggrBgEF
// SIG // BQcBAQSBgzCBgDAkBggrBgEFBQcwAYYYaHR0cDovL29j
// SIG // c3AuZGlnaWNlcnQuY29tMFgGCCsGAQUFBzAChkxodHRw
// SIG // Oi8vY2FjZXJ0cy5kaWdpY2VydC5jb20vRGlnaUNlcnRU
// SIG // cnVzdGVkRzRSU0E0MDk2U0hBMjU2VGltZVN0YW1waW5n
// SIG // Q0EuY3J0MA0GCSqGSIb3DQEBCwUAA4ICAQBVqioa80bz
// SIG // eFc3MPx140/WhSPx/PmVOZsl5vdyipjDd9Rk/BX7NsJJ
// SIG // USx4iGNVCUY5APxp1MqbKfujP8DJAJsTHbCYidx48s18
// SIG // hc1Tna9i4mFmoxQqRYdKmEIrUPwbtZ4IMAn65C3XCYl5
// SIG // +QnmiM59G7hqopvBU2AJ6KO4ndetHxy47JhB8PYOgPvk
// SIG // /9+dEKfrALpfSo8aOlK06r8JSRU1NlmaD1TSsht/fl4J
// SIG // rXZUinRtytIFZyt26/+YsiaVOBmIRBTlClmia+ciPkQh
// SIG // 0j8cwJvtfEiy2JIMkU88ZpSvXQJT657inuTTH4YBZJwA
// SIG // wuladHUNPeF5iL8cAZfJGSOA1zZaX5YWsWMMxkZAO85d
// SIG // NdRZPkOaGK7DycvD+5sTX2q1x+DzBcNZ3ydiK95ByVO5
// SIG // /zQQZ/YmMph7/lxClIGUgp2sCovGSxVK05iQRWAzgOAj
// SIG // 3vgDpPZFR+XOuANCR+hBNnF3rf2i6Jd0Ti7aHh2MWsge
// SIG // mtXC8MYiqE+bvdgcmlHEL5r2X6cnl7qWLoVXwGDneFZ/
// SIG // au/ClZpLEQLIgpzJGgV8unG1TnqZbPTontRamMifv427
// SIG // GFxD9dAq6OJi7ngE273R+1sKqHB+8JeEeOMIA11HLGOo
// SIG // JTiXAdI/Otrl5fbmm9x+LMz/F0xNAKLY1gEOuIvu5uBy
// SIG // VYksJxlh9ncBjDCCBq4wggSWoAMCAQICEAc2N7ckVHzY
// SIG // R6z9KGYqXlswDQYJKoZIhvcNAQELBQAwYjELMAkGA1UE
// SIG // BhMCVVMxFTATBgNVBAoTDERpZ2lDZXJ0IEluYzEZMBcG
// SIG // A1UECxMQd3d3LmRpZ2ljZXJ0LmNvbTEhMB8GA1UEAxMY
// SIG // RGlnaUNlcnQgVHJ1c3RlZCBSb290IEc0MB4XDTIyMDMy
// SIG // MzAwMDAwMFoXDTM3MDMyMjIzNTk1OVowYzELMAkGA1UE
// SIG // BhMCVVMxFzAVBgNVBAoTDkRpZ2lDZXJ0LCBJbmMuMTsw
// SIG // OQYDVQQDEzJEaWdpQ2VydCBUcnVzdGVkIEc0IFJTQTQw
// SIG // OTYgU0hBMjU2IFRpbWVTdGFtcGluZyBDQTCCAiIwDQYJ
// SIG // KoZIhvcNAQEBBQADggIPADCCAgoCggIBAMaGNQZJs8E9
// SIG // cklRVcclA8TykTepl1Gh1tKD0Z5Mom2gsMyD+Vr2EaFE
// SIG // FUJfpIjzaPp985yJC3+dH54PMx9QEwsmc5Zt+FeoAn39
// SIG // Q7SE2hHxc7Gz7iuAhIoiGN/r2j3EF3+rGSs+QtxnjupR
// SIG // PfDWVtTnKC3r07G1decfBmWNlCnT2exp39mQh0YAe9tE
// SIG // QYncfGpXevA3eZ9drMvohGS0UvJ2R/dhgxndX7RUCyFo
// SIG // bjchu0CsX7LeSn3O9TkSZ+8OpWNs5KbFHc02DVzV5huo
// SIG // wWR0QKfAcsW6Th+xtVhNef7Xj3OTrCw54qVI1vCwMROp
// SIG // VymWJy71h6aPTnYVVSZwmCZ/oBpHIEPjQ2OAe3VuJyWQ
// SIG // mDo4EbP29p7mO1vsgd4iFNmCKseSv6De4z6ic/rnH1ps
// SIG // lPJSlRErWHRAKKtzQ87fSqEcazjFKfPKqpZzQmiftkaz
// SIG // nTqj1QPgv/CiPMpC3BhIfxQ0z9JMq++bPf4OuGQq+nUo
// SIG // JEHtQr8FnGZJUlD0UfM2SU2LINIsVzV5K6jzRWC8I41Y
// SIG // 99xh3pP+OcD5sjClTNfpmEpYPtMDiP6zj9NeS3YSUZPJ
// SIG // jAw7W4oiqMEmCPkUEBIDfV8ju2TjY+Cm4T72wnSyPx4J
// SIG // duyrXUZ14mCjWAkBKAAOhFTuzuldyF4wEr1GnrXTdrnS
// SIG // DmuZDNIztM2xAgMBAAGjggFdMIIBWTASBgNVHRMBAf8E
// SIG // CDAGAQH/AgEAMB0GA1UdDgQWBBS6FtltTYUvcyl2mi91
// SIG // jGogj57IbzAfBgNVHSMEGDAWgBTs1+OC0nFdZEzfLmc/
// SIG // 57qYrhwPTzAOBgNVHQ8BAf8EBAMCAYYwEwYDVR0lBAww
// SIG // CgYIKwYBBQUHAwgwdwYIKwYBBQUHAQEEazBpMCQGCCsG
// SIG // AQUFBzABhhhodHRwOi8vb2NzcC5kaWdpY2VydC5jb20w
// SIG // QQYIKwYBBQUHMAKGNWh0dHA6Ly9jYWNlcnRzLmRpZ2lj
// SIG // ZXJ0LmNvbS9EaWdpQ2VydFRydXN0ZWRSb290RzQuY3J0
// SIG // MEMGA1UdHwQ8MDowOKA2oDSGMmh0dHA6Ly9jcmwzLmRp
// SIG // Z2ljZXJ0LmNvbS9EaWdpQ2VydFRydXN0ZWRSb290RzQu
// SIG // Y3JsMCAGA1UdIAQZMBcwCAYGZ4EMAQQCMAsGCWCGSAGG
// SIG // /WwHATANBgkqhkiG9w0BAQsFAAOCAgEAfVmOwJO2b5ip
// SIG // RCIBfmbW2CFC4bAYLhBNE88wU86/GPvHUF3iSyn7cIoN
// SIG // qilp/GnBzx0H6T5gyNgL5Vxb122H+oQgJTQxZ822EpZv
// SIG // xFBMYh0MCIKoFr2pVs8Vc40BIiXOlWk/R3f7cnQU1/+r
// SIG // T4osequFzUNf7WC2qk+RZp4snuCKrOX9jLxkJodskr2d
// SIG // fNBwCnzvqLx1T7pa96kQsl3p/yhUifDVinF2ZdrM8HKj
// SIG // I/rAJ4JErpknG6skHibBt94q6/aesXmZgaNWhqsKRcnf
// SIG // xI2g55j7+6adcq/Ex8HBanHZxhOACcS2n82HhyS7T6NJ
// SIG // uXdmkfFynOlLAlKnN36TU6w7HQhJD5TNOXrd/yVjmScs
// SIG // PT9rp/Fmw0HNT7ZAmyEhQNC3EyTN3B14OuSereU0cZLX
// SIG // JmvkOHOrpgFPvT87eK1MrfvElXvtCl8zOYdBeHo46Zzh
// SIG // 3SP9HSjTx/no8Zhf+yvYfvJGnXUsHicsJttvFXseGYs2
// SIG // uJPU5vIXmVnKcPA3v5gA3yAWTyf7YGcWoWa63VXAOimG
// SIG // sJigK+2VQbc61RWYMbRiCQ8KvYHZE/6/pNHzV9m8BPqC
// SIG // 3jLfBInwAM1dwvnQI38AC+R2AibZ8GV2QqYphwlHK+Z/
// SIG // GqSFD/yYlvZVVCsfgPrA8g4r5db7qS9EFUrnEw4d2zc4
// SIG // GqEr9u3WfPwwggWNMIIEdaADAgECAhAOmxiO+dAt5+/b
// SIG // UOIIQBhaMA0GCSqGSIb3DQEBDAUAMGUxCzAJBgNVBAYT
// SIG // AlVTMRUwEwYDVQQKEwxEaWdpQ2VydCBJbmMxGTAXBgNV
// SIG // BAsTEHd3dy5kaWdpY2VydC5jb20xJDAiBgNVBAMTG0Rp
// SIG // Z2lDZXJ0IEFzc3VyZWQgSUQgUm9vdCBDQTAeFw0yMjA4
// SIG // MDEwMDAwMDBaFw0zMTExMDkyMzU5NTlaMGIxCzAJBgNV
// SIG // BAYTAlVTMRUwEwYDVQQKEwxEaWdpQ2VydCBJbmMxGTAX
// SIG // BgNVBAsTEHd3dy5kaWdpY2VydC5jb20xITAfBgNVBAMT
// SIG // GERpZ2lDZXJ0IFRydXN0ZWQgUm9vdCBHNDCCAiIwDQYJ
// SIG // KoZIhvcNAQEBBQADggIPADCCAgoCggIBAL/mkHNo3rvk
// SIG // XUo8MCIwaTPswqclLskhPfKK2FnC4SmnPVirdprNrnsb
// SIG // hA3EMB/zG6Q4FutWxpdtHauyefLKEdLkX9YFPFIPUh/G
// SIG // nhWlfr6fqVcWWVVyr2iTcMKyunWZanMylNEQRBAu34Lz
// SIG // B4TmdDttceItDBvuINXJIB1jKS3O7F5OyJP4IWGbNOsF
// SIG // xl7sWxq868nPzaw0QF+xembud8hIqGZXV59UWI4MK7dP
// SIG // pzDZVu7Ke13jrclPXuU15zHL2pNe3I6PgNq2kZhAkHnD
// SIG // eMe2scS1ahg4AxCN2NQ3pC4FfYj1gj4QkXCrVYJBMtfb
// SIG // BHMqbpEBfCFM1LyuGwN1XXhm2ToxRJozQL8I11pJpMLm
// SIG // qaBn3aQnvKFPObURWBf3JFxGj2T3wWmIdph2PVldQnaH
// SIG // iZdpekjw4KISG2aadMreSx7nDmOu5tTvkpI6nj3cAORF
// SIG // JYm2mkQZK37AlLTSYW3rM9nF30sEAMx9HJXDj/chsrIR
// SIG // t7t/8tWMcCxBYKqxYxhElRp2Yn72gLD76GSmM9GJB+G9
// SIG // t+ZDpBi4pncB4Q+UDCEdslQpJYls5Q5SUUd0viastkF1
// SIG // 3nqsX40/ybzTQRESW+UQUOsxxcpyFiIJ33xMdT9j7CFf
// SIG // xCBRa2+xq4aLT8LWRV+dIPyhHsXAj6KxfgommfXkaS+Y
// SIG // HS312amyHeUbAgMBAAGjggE6MIIBNjAPBgNVHRMBAf8E
// SIG // BTADAQH/MB0GA1UdDgQWBBTs1+OC0nFdZEzfLmc/57qY
// SIG // rhwPTzAfBgNVHSMEGDAWgBRF66Kv9JLLgjEtUYunpyGd
// SIG // 823IDzAOBgNVHQ8BAf8EBAMCAYYweQYIKwYBBQUHAQEE
// SIG // bTBrMCQGCCsGAQUFBzABhhhodHRwOi8vb2NzcC5kaWdp
// SIG // Y2VydC5jb20wQwYIKwYBBQUHMAKGN2h0dHA6Ly9jYWNl
// SIG // cnRzLmRpZ2ljZXJ0LmNvbS9EaWdpQ2VydEFzc3VyZWRJ
// SIG // RFJvb3RDQS5jcnQwRQYDVR0fBD4wPDA6oDigNoY0aHR0
// SIG // cDovL2NybDMuZGlnaWNlcnQuY29tL0RpZ2lDZXJ0QXNz
// SIG // dXJlZElEUm9vdENBLmNybDARBgNVHSAECjAIMAYGBFUd
// SIG // IAAwDQYJKoZIhvcNAQEMBQADggEBAHCgv0NcVec4X6Cj
// SIG // dBs9thbX979XB72arKGHLOyFXqkauyL4hxppVCLtpIh3
// SIG // bb0aFPQTSnovLbc47/T/gLn4offyct4kvFIDyE7QKt76
// SIG // LVbP+fT3rDB6mouyXtTP0UNEm0Mh65ZyoUi0mcudT6cG
// SIG // AxN3J0TU53/oWajwvy8LpunyNDzs9wPHh6jSTEAZNUZq
// SIG // aVSwuKFWjuyk1T3osdz9HNj0d1pcVIxv76FQPfx2CWiE
// SIG // n2/K2yCNNWAcAgPLILCsWKAOQGPFmCLBsln1VWvPJ6ts
// SIG // ds5vIy30fnFqI2si/xK4VC0nftg62fC2h5b9W9FcrBjD
// SIG // TZ9ztwGpn1eqXijiuZQxggN2MIIDcgIBATB3MGMxCzAJ
// SIG // BgNVBAYTAlVTMRcwFQYDVQQKEw5EaWdpQ2VydCwgSW5j
// SIG // LjE7MDkGA1UEAxMyRGlnaUNlcnQgVHJ1c3RlZCBHNCBS
// SIG // U0E0MDk2IFNIQTI1NiBUaW1lU3RhbXBpbmcgQ0ECEAxN
// SIG // aXJLlPo8Kko9KQeAPVowDQYJYIZIAWUDBAIBBQCggdEw
// SIG // GgYJKoZIhvcNAQkDMQ0GCyqGSIb3DQEJEAEEMBwGCSqG
// SIG // SIb3DQEJBTEPFw0yMzA4MDIwMzExNTlaMCsGCyqGSIb3
// SIG // DQEJEAIMMRwwGjAYMBYEFPOHIk2GM4KSNamUvL2Plun+
// SIG // HHxzMC8GCSqGSIb3DQEJBDEiBCDVjJJK8rNMt2+A8xRc
// SIG // doAfO1SKOAZEPHvwQ1/C6Jng0DA3BgsqhkiG9w0BCRAC
// SIG // LzEoMCYwJDAiBCDH9OG+MiiJIKviJjq+GsT8T+Z4HC1k
// SIG // 0EyAdVegI7W2+jANBgkqhkiG9w0BAQEFAASCAgDLUHTo
// SIG // 0EUL/qK2RBTmXbq7XHymg6NVnWZAKiQY2rWP/neGp3Wa
// SIG // 5gJMUfbgWn2l1TI8NyEwTE6RGI7o2fL+DkQgRWbOoP27
// SIG // +SRid+u9R3mN9IlfJQDiodAqVmK8XMvoDP1fpfzQ4wRU
// SIG // rKf9+gbQSja5rjM5WGFehWJbKN8/qoWRBSUjP5UG8B21
// SIG // pO2KOc4tS/bQ+8c8HpgA9CEONkNLklLiGsFDP5eBuTSi
// SIG // YiliEg15eyfxXba1IdtY7056eelBLEi3jIUrYU5MhvsL
// SIG // QbTsgz7zwB6CsvWtsYAtprDwJAo9BE3i1fl3neBLtNgS
// SIG // mmP88QleQE4pX0foNzdPpTc1EFVMONec3CP62VO1uCO2
// SIG // sK9eG8wwlJm4UnCH4zvjaf8u4ugs86Bukj8DgrzXpr+b
// SIG // obmGloszLRFrqZjnPoTbcMuK+cbHTIXfy8p+sW5rODQ2
// SIG // uIU1Y3MnOnEaByYtarRJmUzz6pdTRmmK1zH8xQ/Eh6GR
// SIG // fpR1eoPyOavsw1y0kbZ4SmK34qn7Sv9Mj5i26A6gbm6t
// SIG // qqWxOGWb6F2va0ZhpmDWzWEbx0+q5uic4t785ek+IWw6
// SIG // F9yq1xEpTwQ/6rk1ivnfeKUnCR1OgIL98gZ1B9ClJKU/
// SIG // OQGhx+VG6BIVmFu9aSf6Q8PjM7+rfUZncwghkz68ZDtZ
// SIG // ExtaN/gZgNijGxXjHg==
// SIG // End signature block
