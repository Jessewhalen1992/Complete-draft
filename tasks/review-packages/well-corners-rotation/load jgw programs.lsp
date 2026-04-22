(setq basePath "C:\\AUTOCAD-SETUP CG\\CG_LISP")

(command "NETLOAD"
    (strcat basePath "\\AUTO UPDATE LABELS\\UpdateDimLabels.DLL")
)

(command "NETLOAD"
    (strcat basePath "\\BEARING DISTANCE\\NewBearingDistanceProgram2025.DLL")
)

(command "NETLOAD"
    (strcat basePath "\\UTM CHECK\\UTMSAVEAS.DLL")
)

(command "NETLOAD"
    (strcat basePath "\\UTM CHECK\\UTMCHECKCLOSE.DLL")
)

(command "NETLOAD"
    (strcat basePath "\\COGO PROGRAM\\SurveyCalculator.DLL")
)
(command "NETLOAD"
    (strcat basePath "\\Compass_20260421_230314.dll")
)
